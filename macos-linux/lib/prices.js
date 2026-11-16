'use strict';
// Per-model API prices: the same table Claude Code uses for /cost and the status line's cost.
// Read from the installed Claude Code's baked model catalog (cached per version in
// ~/.claude/widget/prices.json, shared with the Windows app), else the copy below taken from
// Claude Code 2.1.325. The cost formula matches Claude Code: 1-hour cache writes priced
// separately, x1.1 for US-only inference, fast-mode prices, plus web searches.
const fs = require('fs');
const path = require('path');
const paths = require('./paths');
const fmt = require('./fmt');

const CACHE_FORMAT = 1;
const MARKER = Buffer.from('Hand-maintained baked-in model catalog');

const P = (input, output, write5m, write1h, read) => ({ input, output, write5m, write1h, read, webSearch: 0.01 });

function baked() {
  const tiers = {
    tier_2_10: P(2, 10, 2.5, 4, 0.2),
    tier_2_10_cache_read_0_10: P(2, 10, 2.5, 4, 0.1),
    tier_3_15: P(3, 15, 3.75, 6, 0.3),
    tier_5_25: P(5, 25, 6.25, 10, 0.5),
    tier_15_75: P(15, 75, 18.75, 30, 1.5),
    tier_10_50: P(10, 50, 12.5, 20, 1),
    tier_10_50_cache_read_0_25: P(10, 50, 12.5, 20, 0.25),
    tier_4_20_cache_read_0_20: P(4, 20, 5, 8, 0.2),
    tier_8_40_cache_read_0_20: P(8, 40, 10, 16, 0.2),
    haiku_35: P(0.8, 4, 1, 1.6, 0.08),
    haiku_45: P(1, 5, 1.25, 2, 0.1),
    haiku_55: { ...P(0.1, 0.5, 0.125, 0.2, 0.01), longAbove: 100000, long: P(0.5, 2.5, 0.625, 1, 0.05) },
  };
  const models = [
    ['claude-3-5-haiku', 'haiku_35'], ['claude-haiku-4-5', 'haiku_45'], ['claude-haiku-5-5', 'haiku_55'],
    ['claude-3-5-sonnet', 'tier_3_15'], ['claude-3-7-sonnet', 'tier_3_15'], ['claude-sonnet-4-0', 'tier_3_15'],
    ['claude-sonnet-4-5', 'tier_3_15'], ['claude-sonnet-4-6', 'tier_3_15'], ['claude-sonnet-5', 'tier_2_10'],
    ['claude-sonnet-5-5', 'tier_2_10_cache_read_0_10'], ['claude-opus-4-0', 'tier_15_75'], ['claude-opus-4-1', 'tier_15_75'],
    ['claude-opus-4-5', 'tier_5_25'], ['claude-opus-4-6', 'tier_5_25'], ['claude-opus-4-7', 'tier_5_25'],
    ['claude-opus-4-8', 'tier_5_25'], ['claude-opus-5', 'tier_5_25'], ['claude-opus-5-5', 'tier_4_20_cache_read_0_20'],
    ['claude-fable-5', 'tier_10_50'], ['claude-fable-5-1', 'tier_10_50_cache_read_0_25'], ['claude-fable-5-2', 'tier_8_40_cache_read_0_20'],
    ['claude-mythos-5', 'tier_10_50'], ['claude-mythos-5-1', 'tier_10_50_cache_read_0_25'], ['claude-mythos-5-2', 'tier_8_40_cache_read_0_20'],
  ];
  return { tiers, models, source: 'built-in price table (Claude Code 2.1.325)' };
}

/** Fast-mode prices are code, not catalog data, in Claude Code; copied from 2.1.325. */
function fastPrice(model, standard) {
  switch (model) {
    case 'claude-opus-5-5': return P(8, 40, 10, 16, 0.4);
    case 'claude-fable-5-2':
    case 'claude-mythos-5-2': return P(16, 80, 20, 32, 0.4);
    case 'claude-opus-4-8':
    case 'claude-opus-5': return P(10, 50, 12.5, 20, 1);
    default:
      if (model.includes('opus')) return P(30, 150, 37.5, 60, 3);
      return { input: standard.input * 2, output: standard.output * 2, write5m: standard.write5m * 2, write1h: standard.write1h * 2, read: standard.read * 2, webSearch: standard.webSearch };
  }
}

function semverKey(v) { return v.split('.').map((x) => x.padStart(6, '0')).join('.'); }

/** Finds the installed Claude Code (native binary or npm cli.js). Returns { file, version }. */
function findClaude() {
  const native = path.join(paths.home, '.local', 'share', 'claude', 'versions');
  try {
    const versions = fs.readdirSync(native).filter((n) => /^\d+\.\d+\.\d+$/.test(n)).sort((a, b) => (semverKey(a) < semverKey(b) ? 1 : -1));
    for (const v of versions) {
      const f = path.join(native, v);
      if (fs.statSync(f).isFile()) return { file: f, version: v };
    }
  } catch { /* not a native install */ }

  const candidates = [];
  for (const dir of (process.env.PATH || '').split(path.delimiter)) {
    if (!dir) continue;
    for (const name of process.platform === 'win32' ? ['claude.exe', 'claude.cmd'] : ['claude']) candidates.push(path.join(dir, name));
  }
  const pkg = path.join('@anthropic-ai', 'claude-code', 'cli.js');
  candidates.push(
    path.join(paths.claudeDir(), 'local', 'node_modules', pkg),
    path.join('/usr/local/lib/node_modules', pkg),
    path.join('/opt/homebrew/lib/node_modules', pkg),
    path.join(paths.home, '.npm-global', 'lib', 'node_modules', pkg),
  );
  for (const c of candidates) {
    try {
      const real = fs.realpathSync(c);
      const st = fs.statSync(real);
      if (!st.isFile() || st.size < 100000) continue; // a launcher script, not the real thing
      let version = `size-${st.size}`;
      const pj = paths.readJson(path.join(path.dirname(real), 'package.json'));
      if (pj && pj.name === '@anthropic-ai/claude-code' && pj.version) version = pj.version;
      return { file: real, version };
    } catch { /* not there */ }
  }
  return null;
}

/** Finds Claude Code's baked model catalog ({"//":"Hand-maintained baked-in model catalog ...",
 *  pricing_tiers: ..., models: [...]}) inside its binary or cli.js. */
function readCatalogText(file) {
  const fd = fs.openSync(file, 'r');
  try {
    const size = fs.fstatSync(fd).size;
    const chunk = Buffer.allocUnsafe(8 << 20);
    let pos = 0;
    let found = -1;
    while (pos < size && found < 0) {
      const n = fs.readSync(fd, chunk, 0, chunk.length, pos);
      if (n <= 0) break;
      const at = chunk.subarray(0, n).indexOf(MARKER);
      if (at >= 0) found = pos + at;
      else pos += Math.max(1, n - MARKER.length);
    }
    if (found < 0) return null;
    const start = Math.max(0, found - 64);
    const win = Buffer.alloc(256 * 1024);
    const got = fs.readSync(fd, win, 0, win.length, start);
    const text = win.subarray(0, got).toString('utf8');
    const m = text.indexOf('Hand-maintained');
    const open = m < 0 ? -1 : text.lastIndexOf('{', m);
    if (open < 0) return null;
    let depth = 0;
    let quote = null;
    for (let i = open; i < text.length; i++) {
      const c = text[i];
      if (quote) {
        if (c === '\\') { i++; continue; }
        if (c === quote) quote = null;
        continue;
      }
      if (c === '"' || c === "'" || c === '`') { quote = c; continue; }
      if (c === '{') depth++;
      else if (c === '}' && --depth === 0) return text.slice(open, i + 1);
    }
    return null;
  } finally {
    fs.closeSync(fd);
  }
}

function parseCatalog(text) {
  const N = '([0-9.eE+-]+)';
  const k = (name) => `"?${name}"?\\s*:\\s*`;
  const tierRx = new RegExp(
    `"?(\\w+)"?\\s*:\\s*\\{\\s*${k('input')}${N}\\s*,\\s*${k('output')}${N}\\s*,\\s*${k('cache_write_5m')}${N}\\s*,\\s*` +
    `${k('cache_write_1h')}${N}\\s*,\\s*${k('cache_read')}${N}\\s*,\\s*${k('web_search')}${N}` +
    `(?:\\s*,\\s*${k('long_prompt')}\\{\\s*${k('above_prompt_tokens')}${N}\\s*,\\s*${k('input')}${N}\\s*,\\s*${k('output')}${N}\\s*,\\s*` +
    `${k('cache_write_5m')}${N}\\s*,\\s*${k('cache_write_1h')}${N}\\s*,\\s*${k('cache_read')}${N}\\s*\\})?\\s*\\}`, 'g');
  const modelsAt = text.search(/"?models"?\s*:\s*\[/);
  if (modelsAt < 0) return null;
  const tiers = {};
  for (const m of text.slice(0, modelsAt).matchAll(tierRx)) {
    const n = (i) => Number(m[i]);
    const t = { input: n(2), output: n(3), write5m: n(4), write1h: n(5), read: n(6), webSearch: n(7) };
    if (m[8] !== undefined) {
      t.longAbove = n(8);
      t.long = { input: n(9), output: n(10), write5m: n(11), write1h: n(12), read: n(13), webSearch: t.webSearch };
    }
    tiers[m[1]] = t;
  }
  const rest = text.slice(modelsAt);
  const ids = [...rest.matchAll(/\{\s*"?id"?\s*:\s*"([^"]+)"/g)];
  const models = [];
  ids.forEach((m, i) => {
    const seg = rest.slice(m.index, i + 1 < ids.length ? ids[i + 1].index : rest.length);
    const pr = /"?pricing"?\s*:\s*"([^"]+)"/.exec(seg);
    if (pr) models.push([m[1], pr[1]]);
  });
  const ok = Object.keys(tiers).length >= 3 && models.length >= 5 && models.every(([, t]) => tiers[t]);
  return ok ? { tiers, models } : null;
}

function toCache(book, claude, size) {
  const tierJson = (t) => {
    const o = { input: t.input, output: t.output, cache_write_5m: t.write5m, cache_write_1h: t.write1h, cache_read: t.read, web_search: t.webSearch };
    if (t.long) { o.long_above = t.longAbove; o.long = tierJson(t.long); }
    return o;
  };
  const tiers = {};
  for (const [name, t] of Object.entries(book.tiers)) tiers[name] = tierJson(t);
  return {
    format: CACHE_FORMAT, claude_version: claude.version, binary_size: size,
    note: "Read from Claude Code's baked model catalog. USD per million tokens.",
    tiers, models: Object.fromEntries(book.models),
  };
}

function fromCache(o) {
  if (!o || !o.tiers || !o.models) return null;
  const tier = (t) => {
    const r = { input: t.input || 0, output: t.output || 0, write5m: t.cache_write_5m || 0, write1h: t.cache_write_1h || 0, read: t.cache_read || 0, webSearch: t.web_search || 0 };
    if (t.long) { r.longAbove = t.long_above || 0; r.long = { ...tier(t.long), webSearch: r.webSearch }; }
    return r;
  };
  const tiers = {};
  for (const [name, t] of Object.entries(o.tiers)) tiers[name] = tier(t);
  const models = Object.entries(o.models).filter(([, t]) => tiers[t]);
  return Object.keys(tiers).length >= 3 && models.length >= 5 ? { tiers, models } : null;
}

let book = null;
let loadedVersion = null;
let lastCheck = 0;

function load() {
  const claude = findClaude();
  let size = 0;
  if (claude) { try { size = fs.statSync(claude.file).size; } catch { /* ignore */ } }
  const cache = paths.readJson(paths.pricesFile());
  if (claude && cache && cache.format === CACHE_FORMAT && cache.claude_version === claude.version && cache.binary_size === size) {
    const b = fromCache(cache);
    if (b) { loadedVersion = claude.version; return { ...b, source: `Claude Code ${claude.version} price table` }; }
  }
  if (claude) {
    try {
      const text = readCatalogText(claude.file);
      const b = text && parseCatalog(text);
      if (b) {
        loadedVersion = claude.version;
        try { paths.writeAtomic(paths.pricesFile(), JSON.stringify(toCache(b, claude, size), null, 2)); } catch { /* read-only */ }
        return { ...b, source: `Claude Code ${claude.version} price table` };
      }
    } catch (e) { paths.log('prices', e); }
  }
  const b = fromCache(cache);
  if (b) return { ...b, source: `cached price table (Claude Code ${cache.claude_version})` };
  return baked();
}

function ensure() { if (!book) book = load(); return book; }

/** Re-read prices if Claude Code updated itself (checked every 10 minutes). */
function checkForUpdate() {
  const now = Date.now();
  if (now - lastCheck < 10 * 60 * 1000) return;
  lastCheck = now;
  const c = findClaude();
  if (book && c && c.version !== loadedVersion) book = null;
  ensure();
}

function cleanModel(model) { return (fmt.baseModel(model) || '').replace(/-\d{8}$/, ''); }

/** Standard price for a model; exact=false when the model is unknown and a family match
 *  (or Claude Code's own $5/$25 fallback) was used. */
function priceFor(model) {
  const b = ensure();
  const m = cleanModel(model);
  let tier = (b.models.find(([id]) => id === m) || [])[1];
  if (!tier) {
    const pre = b.models.filter(([id]) => m.startsWith(`${id}-`)).sort((x, y) => y[0].length - x[0].length)[0];
    if (pre) tier = pre[1];
  }
  if (tier) return { price: b.tiers[tier], exact: true };
  for (const family of ['haiku', 'sonnet', 'opus', 'fable', 'mythos']) {
    if (!m.includes(family)) continue;
    const newest = b.models.filter(([id]) => id.includes(family)).pop();
    if (newest) return { price: b.tiers[newest[1]], exact: false };
  }
  return { price: P(5, 25, 6.25, 10, 0.5), exact: false };
}

/** Cost in USD of one API response, computed the way Claude Code does. */
function cost(model, usage) {
  let { price, exact } = priceFor(model);
  if (usage.speed === 'fast') price = fastPrice(cleanModel(model), price);
  const input = usage.input_tokens || 0;
  const output = usage.output_tokens || 0;
  const read = usage.cache_read_input_tokens || 0;
  const write = usage.cache_creation_input_tokens || 0;
  const oneHour = Math.max(0, Math.min(write, (usage.cache_creation && usage.cache_creation.ephemeral_1h_input_tokens) || 0));
  if (price.long && input + read + write > price.longAbove) price = price.long;
  let c = (input * price.input + output * price.output + read * price.read + oneHour * price.write1h + (write - oneHour) * price.write5m) / 1e6;
  if (usage.inference_geo === 'us') c *= 1.1;
  c += ((usage.server_tool_use && usage.server_tool_use.web_search_requests) || 0) * price.webSearch;
  return { cost: c, exact };
}

module.exports = { cost, priceFor, checkForUpdate, source: () => ensure().source, findClaude, parseCatalog, readCatalogText };
