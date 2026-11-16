'use strict';
// Builds a snapshot of every live Claude Code session from ~/.claude/sessions, transcripts,
// hook markers and status line captures. Keep one instance: transcripts are read incrementally.
const fs = require('fs');
const path = require('path');
const paths = require('./paths');
const fmt = require('./fmt');
const prices = require('./prices');
const procs = require('./procs');
const live = require('./live');
const { TranscriptFile, emptyTokens, addTokens, total } = require('./transcripts');

const DONE_WINDOW_MS = 10 * 60 * 1000;
const RANK = { waiting: 0, busy: 1, done: 2, idle: 3 };

function list(dir, filter) {
  try { return fs.readdirSync(dir).filter(filter).map((n) => path.join(dir, n)); } catch { return []; }
}

function mtime(f) { try { return fs.statSync(f).mtimeMs; } catch { return 0; } }

function isDir(f) { try { return fs.statSync(f).isDirectory(); } catch { return false; } }

class Collector {
  constructor() {
    this.files = new Map();          // path -> TranscriptFile
    this.mainBySession = new Map();  // session id -> main transcript path
    this.subsBySession = new Map();  // session id -> [subagent transcript paths]
    this.hostCache = new Map();
    this.captures = new Map();       // status line capture file -> { mtimeMs, data }
    this.lastScan = 0;
    this.lastSetup = 0;
    this.setup = { hooks: false, statusLine: false, otherStatusLine: null };
  }

  collect() {
    const now = Date.now();
    prices.checkForUpdate();
    const liveSessions = this.readLiveSessions();
    const liveIds = new Set(liveSessions.map((o) => o.sessionId || ''));
    const missing = [...liveIds].some((id) => id && !this.mainBySession.has(id));
    if (now - this.lastScan > 10000 || (missing && now - this.lastScan > 2000)) this.scan(liveIds, now);
    for (const f of this.files.values()) f.update();

    // Tokens and cost per day, last 7 days.
    const byDay = new Map();
    for (const f of this.files.values()) f.addByDay(byDay);
    const days = [];
    for (let k = 6; k >= 0; k--) {
      const d = new Date(now);
      d.setDate(d.getDate() - k);
      const key = fmt.localDay(d.getTime());
      days.push({ date: key, weekday: fmt.DAYS[d.getDay()], tokens: byDay.get(key) || emptyTokens() });
    }

    const status = this.readStatusLines(now);
    let table;
    const sessions = liveSessions.map((o) => {
      const s = {
        pid: o.pid, sessionId: o.sessionId || '', cwd: o.cwd || '', name: o.name || null,
        kind: o.kind || '', entrypoint: o.entrypoint || '', version: o.version || null,
        status: String(o.status || 'idle').toLowerCase(), waitingFor: o.waitingFor || null,
        startedAt: o.startedAt || 0, statusSince: o.statusUpdatedAt || o.updatedAt || o.startedAt || 0,
        tokens: emptyTokens(), subagentTokens: emptyTokens(), contextTokens: -1, model: null,
        aiTitle: null, lastPrompt: null, currentTool: null, runningTools: 0,
      };
      s.project = path.basename((s.cwd || '').replace(/[\\/]+$/, '')) || s.cwd;
      if (!s.name) s.name = s.project;

      const hostKey = `${s.pid}:${o.procStart || ''}`;
      if (!this.hostCache.has(hostKey)) {
        if (table === undefined) table = procs.processTable();
        this.hostCache.set(hostKey, procs.hostLabel(s.pid, s.entrypoint, table));
      }
      s.host = this.hostCache.get(hostKey);

      const main = this.files.get(this.mainBySession.get(s.sessionId));
      if (main) {
        s.transcript = main.file;
        s.tokens = main.totals();
        s.aiTitle = main.aiTitle;
        s.lastPrompt = main.lastPrompt;
        if (main.contextTokens >= 0) { s.contextTokens = main.contextTokens; s.model = main.contextModel; }
      }
      for (const p of this.subsBySession.get(s.sessionId) || []) {
        const sf = this.files.get(p);
        if (sf) addTokens(s.subagentTokens, sf.totals());
      }

      // Title: a name you gave it (/rename), else Claude's AI title, else your last prompt,
      // else the folder. Never the auto-generated slug ("my-project-ea").
      if (o.nameSource === 'user' && o.name) s.title = o.name;
      else if (s.aiTitle) s.title = s.aiTitle;
      else if (s.lastPrompt) s.title = fmt.oneLine(s.lastPrompt, 70);
      else s.title = s.project;

      if (s.status === 'waiting') s.state = 'waiting';
      else if (s.status !== 'idle') s.state = 'busy';
      else if (now - s.statusSince < DONE_WINDOW_MS && s.statusSince - s.startedAt > 5000 && s.tokens.requests > 0) s.state = 'done';
      else s.state = 'idle';

      this.resolveContextWindow(s, status);
      s.modelLabel = fmt.modelLabel(s.model);
      if (s.state === 'busy' || s.state === 'waiting') {
        const runs = live.running(s.sessionId);
        s.runningTools = runs.length;
        s.currentTool = runs[0] || null;
      }
      return s;
    });

    // No exact window size known yet: if any session of a model is past 200k tokens, that
    // model has a larger window, so use 1M for all its sessions.
    const big = new Set(sessions.filter((x) => x.contextTokens > 200000 && x.model).map((x) => fmt.baseModel(x.model)));
    for (const x of sessions) if (!x.contextExact && x.model && big.has(fmt.baseModel(x.model))) x.contextWindow = 1000000;

    sessions.sort((a, b) => RANK[a.state] - RANK[b.state] || a.startedAt - b.startedAt);
    if (now - this.lastSetup > 10000) { this.lastSetup = now; this.checkSetup(); }

    const count = (st) => sessions.filter((x) => x.state === st).length;
    return {
      generatedAt: now,
      sessions,
      counts: { waiting: count('waiting'), busy: count('busy'), done: count('done'), idle: count('idle') },
      today: days[days.length - 1].tokens,
      days,
      limits: status.limits,
      setup: this.setup,
      priceSource: prices.source(),
      platform: process.platform,
    };
  }

  readLiveSessions() {
    const out = [];
    for (const f of list(paths.sessionsDir(), (n) => n.endsWith('.json'))) {
      const o = paths.readJson(f);
      if (!o || !(o.pid > 0) || o.spare === true) continue; // spare = pre-warmed, not a session
      if (!procs.isSameProcess(o.pid, o.procStart)) continue; // stale file from a dead process
      out.push(o);
    }
    return out;
  }

  scan(liveIds, now) {
    this.lastScan = now;
    this.mainBySession.clear();
    this.subsBySession.clear();
    const keep = new Set();
    const cutoff = now - 8 * 24 * 3600 * 1000;
    const track = (f) => { keep.add(f); if (!this.files.has(f)) this.files.set(f, new TranscriptFile(f)); };
    for (const proj of list(paths.projectsDir(), () => true).filter(isDir)) {
      for (const f of list(proj, (n) => n.endsWith('.jsonl'))) {
        const sid = path.basename(f, '.jsonl');
        if (!liveIds.has(sid) && mtime(f) < cutoff) continue;
        track(f);
        this.mainBySession.set(sid, f);
      }
      for (const sessionDir of list(proj, () => true).filter(isDir)) {
        const sid = path.basename(sessionDir);
        for (const f of list(path.join(sessionDir, 'subagents'), (n) => n.endsWith('.jsonl'))) {
          if (!liveIds.has(sid) && mtime(f) < cutoff) continue;
          track(f);
          if (!this.subsBySession.has(sid)) this.subsBySession.set(sid, []);
          this.subsBySession.get(sid).push(f);
        }
      }
    }
    for (const k of [...this.files.keys()]) if (!keep.has(k)) this.files.delete(k);
  }

  /** Reads status line captures (written by the hook) for plan limits and context window
   *  sizes, and writes the line the macOS/Linux shell hook prints (<session>.txt). */
  readStatusLines(now) {
    const dir = paths.statuslineDir();
    const seen = new Set();
    for (const f of list(dir, (n) => n.endsWith('.json'))) {
      seen.add(f);
      const m = mtime(f);
      const prev = this.captures.get(f);
      if (prev && prev.mtimeMs === m) continue;
      if (now - m > 7 * 24 * 3600 * 1000) { // old session: tidy up
        for (const g of [f, f.replace(/\.json$/, '.txt')]) { try { fs.unlinkSync(g); } catch { /* gone */ } }
        continue;
      }
      const data = paths.readJson(f);
      if (!data) continue;
      this.captures.set(f, { mtimeMs: m, data });
      if (process.platform !== 'win32') {
        try { fs.writeFileSync(f.replace(/\.json$/, '.txt'), statusText(data)); } catch { /* read-only */ }
      }
    }
    for (const f of [...this.captures.keys()]) if (!seen.has(f)) this.captures.delete(f);

    // Plan limits: newest capture that has them (or limits.json from the Windows hook).
    let limits = null;
    const consider = (rl, at) => {
      if (!rl || (!rl.five_hour && !rl.seven_day)) return;
      if (limits && limits.capturedAt >= at) return;
      limits = { capturedAt: at, fiveHour: windowOf(rl.five_hour, now), sevenDay: windowOf(rl.seven_day, now) };
    };
    for (const { mtimeMs, data } of this.captures.values()) consider(data.rate_limits, data._captured_at || mtimeMs);
    const lj = paths.readJson(paths.limitsFile());
    if (lj) consider(lj, lj.captured_at || 0);
    if (limits && !limits.fiveHour && !limits.sevenDay) limits = null;

    const models = { ...(paths.readJson(paths.modelsFile()) || {}) };
    const bySession = new Map();
    for (const { data } of this.captures.values()) {
      const id = fmt.baseModel(data.model && data.model.id);
      const size = data.context_window && data.context_window.context_window_size;
      if (id && size > 0) models[id] = size;
      if (data.session_id) bySession.set(data.session_id, data);
    }
    return { limits, models, bySession };
  }

  resolveContextWindow(s, status) {
    let size = 0;
    let exact = false;
    const cap = status.bySession.get(s.sessionId);
    if (cap) {
      const capModel = cap.model && cap.model.id;
      const w = cap.context_window && cap.context_window.context_window_size;
      if (w > 0 && (!s.model || fmt.baseModel(capModel) === fmt.baseModel(s.model))) { size = w; exact = true; }
      if (!s.model) s.model = capModel || null;
    }
    if (!size && s.model && status.models[fmt.baseModel(s.model)] > 0) { size = status.models[fmt.baseModel(s.model)]; exact = true; }
    if (!size && s.contextTokens >= 0) size = s.contextTokens > 200000 ? 1000000 : 200000;
    s.contextWindow = size;
    s.contextExact = exact;
    s.contextPct = s.contextTokens >= 0 && size > 0 ? (100 * s.contextTokens) / size : null;
  }

  checkSetup() {
    const root = paths.readJson(paths.settingsFile());
    const ours = (g) => g && Array.isArray(g.hooks) && g.hooks.some((h) => JSON.stringify(h).includes('claude-widget-hook'));
    this.setup = { hooks: false, statusLine: false, otherStatusLine: null };
    if (!root) return;
    if (root.hooks && typeof root.hooks === 'object') {
      this.setup.hooks = Object.values(root.hooks).some((groups) => Array.isArray(groups) && groups.some(ours));
    }
    const cmd = root.statusLine && root.statusLine.command;
    if (cmd) {
      if (cmd.includes('claude-widget-hook')) this.setup.statusLine = true;
      else this.setup.otherStatusLine = cmd;
    }
  }
}

function windowOf(w, now) {
  if (!w || typeof w.used_percentage !== 'number') return null;
  const resetsAt = w.resets_at || 0;
  if (resetsAt > 0 && resetsAt * 1000 < now) return null; // already reset: old number is stale
  return { usedPct: w.used_percentage, resetsAt };
}

/** "Opus 5.5 · ctx 34% · 5h 16% · 7d 12%" */
function statusText(data) {
  const parts = [];
  if (data.model && data.model.display_name) parts.push(data.model.display_name);
  const cw = data.context_window || {};
  if (typeof cw.used_percentage === 'number') parts.push(`ctx ${Math.round(cw.used_percentage)}%`);
  const rl = data.rate_limits || {};
  if (rl.five_hour && typeof rl.five_hour.used_percentage === 'number') parts.push(`5h ${Math.round(rl.five_hour.used_percentage)}%`);
  if (rl.seven_day && typeof rl.seven_day.used_percentage === 'number') parts.push(`7d ${Math.round(rl.seven_day.used_percentage)}%`);
  return parts.join(' · ');
}

module.exports = { Collector, total, statusText };
