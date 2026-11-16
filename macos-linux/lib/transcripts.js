'use strict';
// Incremental reader for Claude Code transcripts (~/.claude/projects/<project>/<session>.jsonl
// and <session>/subagents/*.jsonl). Only appended bytes are read on each update. One API
// response is written as several "assistant" records sharing a requestId, so usage is
// counted once per request.
const fs = require('fs');
const prices = require('./prices');
const fmt = require('./fmt');

const TYPE_ASSISTANT = Buffer.from('"type":"assistant"');
const TYPE_TITLE = Buffer.from('"type":"ai-title"');
const TYPE_PROMPT = Buffer.from('"type":"last-prompt"');

function emptyTokens() {
  return { input: 0, output: 0, cacheWrite: 0, cacheRead: 0, requests: 0, cost: 0, costApprox: false };
}

function addTokens(a, b) {
  a.input += b.input; a.output += b.output; a.cacheWrite += b.cacheWrite; a.cacheRead += b.cacheRead;
  a.requests += b.requests; a.cost += b.cost; a.costApprox = a.costApprox || b.costApprox;
  return a;
}

function total(t) { return t.input + t.output + t.cacheWrite + t.cacheRead; }

class TranscriptFile {
  constructor(file) {
    this.file = file;
    this.reset();
  }

  reset() {
    this.offset = 0;
    this.partial = Buffer.alloc(0);
    this.reqs = new Map(); // requestId -> { day, t }
    this.contextTokens = -1;
    this.contextModel = null;
    this.aiTitle = null;
    this.lastPrompt = null;
  }

  /** Reads whatever was appended since last time. Returns true if anything changed. */
  update() {
    let size;
    try { size = fs.statSync(this.file).size; } catch { return false; }
    if (size < this.offset) this.reset(); // rewritten: start over
    if (size === this.offset) return false;
    let fd;
    try { fd = fs.openSync(this.file, 'r'); } catch { return false; }
    try {
      const chunk = Buffer.allocUnsafe(Math.min(size - this.offset, 1 << 20));
      while (this.offset < size) {
        const n = fs.readSync(fd, chunk, 0, Math.min(chunk.length, size - this.offset), this.offset);
        if (n <= 0) break;
        this.offset += n;
        this.feed(chunk.subarray(0, n));
      }
    } catch { /* partial read: continue next time */ } finally {
      fs.closeSync(fd);
    }
    return true;
  }

  feed(buf) {
    const data = this.partial.length ? Buffer.concat([this.partial, buf]) : buf;
    let start = 0;
    for (;;) {
      const nl = data.indexOf(10, start);
      if (nl < 0) break;
      this.ingest(data.subarray(start, nl));
      start = nl + 1;
    }
    this.partial = Buffer.from(data.subarray(start)); // copy: the chunk buffer is reused
  }

  ingest(line) {
    if (line.length === 0) return;
    const kind = line.indexOf(TYPE_ASSISTANT) >= 0 ? 1 : line.indexOf(TYPE_TITLE) >= 0 ? 2 : line.indexOf(TYPE_PROMPT) >= 0 ? 3 : 0;
    if (!kind) return;
    let rec;
    try { rec = JSON.parse(line.toString('utf8')); } catch { return; }
    if (rec.type === 'ai-title') {
      if (rec.aiTitle && rec.aiTitle.trim()) this.aiTitle = rec.aiTitle.trim();
    } else if (rec.type === 'last-prompt') {
      if (rec.lastPrompt && rec.lastPrompt.trim()) this.lastPrompt = rec.lastPrompt.trim();
    } else if (rec.type === 'assistant') {
      this.ingestAssistant(rec);
    }
  }

  ingestAssistant(rec) {
    const msg = rec.message || {};
    const u = msg.usage;
    if (!u) return;
    const model = msg.model || '';
    if (model === '<synthetic>') return;
    const c = prices.cost(model, u);
    const t = {
      input: u.input_tokens || 0,
      output: u.output_tokens || 0,
      cacheWrite: u.cache_creation_input_tokens || 0,
      cacheRead: u.cache_read_input_tokens || 0,
      requests: 1,
      cost: c.cost,
      costApprox: !c.exact,
    };
    const when = Date.parse(rec.timestamp);
    const day = Number.isFinite(when) ? fmt.localDay(when) : null;
    const key = rec.requestId || msg.id || rec.uuid || `line-${this.reqs.size}`;
    const prev = this.reqs.get(key);
    if (prev) {
      // Same response written again: keep the largest numbers.
      for (const f of ['input', 'output', 'cacheWrite', 'cacheRead', 'cost']) prev.t[f] = Math.max(prev.t[f], t[f]);
    } else {
      this.reqs.set(key, { day, t });
    }
    if (rec.isSidechain !== true) {
      this.contextTokens = t.input + t.cacheWrite + t.cacheRead;
      this.contextModel = model;
    }
  }

  totals() {
    const t = emptyTokens();
    for (const r of this.reqs.values()) addTokens(t, r.t);
    return t;
  }

  addByDay(map) {
    for (const r of this.reqs.values()) {
      if (!r.day) continue;
      if (!map.has(r.day)) map.set(r.day, emptyTokens());
      addTokens(map.get(r.day), r.t);
    }
  }
}

module.exports = { TranscriptFile, emptyTokens, addTokens, total };
