'use strict';
// Renders snapshots from the main process. Rows are updated in place (keyed by pid) so the
// spinner animations keep running. Text from transcripts is only ever set via textContent.
const api = window.widget;
const $ = (id) => document.getElementById(id);

// ---------------------------------------------------------------- formatting (mirrors lib/fmt.js)
const fmt = {
  tokens(n) {
    const r1 = (x) => String(Math.round(x * 10) / 10), r2 = (x) => String(Math.round(x * 100) / 100);
    if (n < 1000) return String(n);
    if (n < 10000) return `${r1(n / 1000)}k`;
    if (n < 1e6) return `${Math.floor(n / 1000)}k`;
    if (n < 1e7) return `${r2(n / 1e6)}M`;
    if (n < 1e9) return `${r1(n / 1e6)}M`;
    return `${r2(n / 1e9)}B`;
  },
  money(usd) {
    if (!(usd > 0)) return '$0';
    if (usd < 0.01) return '<$0.01';
    if (usd < 1000) return `$${usd.toFixed(2)}`;
    if (usd < 100000) return `$${Math.round(usd).toLocaleString('en-US')}`;
    return `$${Math.round(usd / 1000)}k`;
  },
  duration(ms) {
    const s = Math.max(0, Math.floor(ms / 1000));
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ${m % 60}m`;
    return `${Math.floor(h / 24)}d ${h % 24}h`;
  },
  ago(ms) { return ms < 60000 ? 'just now' : `${fmt.duration(ms)} ago`; },
  resetsIn(sec, now) {
    const ms = sec * 1000 - now;
    if (ms <= 0) return 'now';
    if (ms < 86400000) return `in ${fmt.duration(ms)}`;
    const d = new Date(sec * 1000);
    return `${['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][d.getDay()]} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  },
  oneLine(s, max) {
    if (!s || !s.trim()) return '';
    const r = s.trim().replace(/\s+/g, ' ');
    return r.length > max ? `${r.slice(0, max - 1)}…` : r;
  },
  total(t) { return t.input + t.output + t.cacheWrite + t.cacheRead; },
};

function toolLabel(tool) {
  if (tool && tool.startsWith('mcp__')) {
    const rest = tool.slice(5), sep = rest.indexOf('__');
    if (sep > 0) return `${rest.slice(0, sep)} · ${rest.slice(sep + 2)}`;
  }
  return tool || '';
}

// ---------------------------------------------------------------- icons (constant SVG only)
const ICONS = {
  terminal: '<polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/>',
  file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/>',
  pencil: '<path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/>',
  search: '<circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>',
  globe: '<circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
  people: '<path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  bolt: '<polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/>',
  link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
  play: '<polygon points="6 4 19 12 6 20 6 4"/>',
  check: '<polyline points="20 6 9 17 4 12"/>',
  alert: '<path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>',
  message: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
  up: '<polyline points="18 15 12 9 6 15"/>',
  down: '<polyline points="6 9 12 15 18 9"/>',
  minus: '<line x1="5" y1="12" x2="19" y2="12"/>',
};
const svg = (name, size = 13, stroke = 2.2) =>
  `<svg class="ico" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round">${ICONS[name]}</svg>`;

function toolIcon(tool) {
  switch (tool) {
    case 'Bash': case 'PowerShell': return 'terminal';
    case 'Read': return 'file';
    case 'Write': case 'Edit': case 'MultiEdit': case 'NotebookEdit': return 'pencil';
    case 'Grep': case 'Glob': return 'search';
    case 'WebFetch': case 'WebSearch': return 'globe';
    case 'Agent': case 'Task': return 'people';
    case 'Skill': return 'bolt';
    default: return tool && tool.startsWith('mcp__') ? 'link' : 'play';
  }
}

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function bar(pct, height) {
  const b = el('div', 'bar');
  if (height) b.style.height = `${height}px`;
  const i = el('i');
  b.appendChild(i);
  setBar(b, pct);
  return b;
}

function setBar(b, pct) {
  const i = b.firstChild;
  const p = Math.max(0, Math.min(100, pct));
  i.style.width = `${p}%`;
  i.className = p <= 0 ? 'zero' : p >= 90 ? 'hot' : p >= 75 ? 'warn' : '';
}

// ---------------------------------------------------------------- state
let config = { compact: false, platform: '' };
let room = 2000;
let last = null;
let sentHeight = 0;
const sigs = {};
const rows = new Map();

$('btn-compact').innerHTML = svg('up', 12, 2.4);
$('btn-hide').innerHTML = svg('minus', 12, 2.4);
$('btn-compact').onclick = () => api.toggleCompact();
$('btn-hide').onclick = () => api.hide();
$('list').addEventListener('scroll', updateMoreHint);
window.addEventListener('wheel', (e) => {
  if (!e.ctrlKey && !e.metaKey) return;
  e.preventDefault();
  api.zoom(e.deltaY < 0 ? 0.05 : -0.05);
}, { passive: false });

// ---------------------------------------------------------------- rendering

function renderChips(s) {
  const c = s.counts;
  const sig = `${s.sessions.length}/${c.waiting}/${c.busy}/${c.done}/${c.idle}`;
  if (sigs.chips === sig) return;
  sigs.chips = sig;
  const box = $('chips');
  box.textContent = '';
  if (!s.sessions.length) { box.appendChild(el('span', 'pill none', 'no sessions')); return; }
  for (const k of ['waiting', 'busy', 'done', 'idle']) if (c[k]) box.appendChild(el('span', `pill ${k}`, `${c[k]} ${k}`));
}

function renderLimits(s) {
  const L = s.limits;
  const now = s.generatedAt;
  const win = (w) => (w ? `${Math.round(w.usedPct)}/${fmt.resetsIn(w.resetsAt, now)}` : '-');
  const sig = L ? `${win(L.fiveHour)}|${win(L.sevenDay)}|${fmt.ago(now - L.capturedAt)}` : `none|${s.setup.statusLine}`;
  if (sigs.limits === sig) return;
  sigs.limits = sig;
  const box = $('limits');
  box.textContent = '';
  box.style.display = L || s.setup.statusLine ? '' : 'none'; // the footer explains when it's off
  if (!L) { box.appendChild(el('div', 'msg', 'Plan limits appear once a terminal Claude session refreshes its status line.')); return; }
  const head = el('div', 'limits-head');
  head.appendChild(el('span', 'caps', 'PLAN USAGE'));
  head.appendChild(el('span', 'ago', `updated ${fmt.ago(now - L.capturedAt)}`));
  box.appendChild(head);
  for (const [label, w] of [['5-hour', L.fiveHour], ['Weekly', L.sevenDay]]) {
    if (!w) continue;
    const row = el('div', 'limit');
    row.appendChild(el('span', 'label', label));
    row.appendChild(bar(w.usedPct, 8));
    row.appendChild(el('span', 'pct', `${Math.round(w.usedPct)}%`));
    row.appendChild(el('span', 'reset', `resets ${fmt.resetsIn(w.resetsAt, now)}`));
    box.appendChild(row);
  }
}

function renderUsage(s) {
  const t = s.today;
  const sig = `${fmt.total(t)}|${t.requests}|${t.cost.toFixed(2)}|${s.days.map((d) => d.tokens.cost.toFixed(2)).join(',')}`;
  if (sigs.usage === sig) return;
  sigs.usage = sig;
  const box = $('usage');
  box.textContent = '';
  const wrap = el('div', 'usage');
  const left = el('div');
  left.appendChild(el('div', 'caps', 'TODAY'));
  const big = el('div', 'big');
  big.appendChild(el('b', '', fmt.money(t.cost)));
  big.appendChild(el('span', '', 'est. cost'));
  big.title = `Estimated at API prices, using ${s.priceSource}.\nOn a Pro / Max / Team plan this is not what you pay; the plan limits above are what count.\n\n` +
    `Input ${fmt.tokens(t.input)}  ·  Output ${fmt.tokens(t.output)}\nCache write ${fmt.tokens(t.cacheWrite)}  ·  Cache read ${fmt.tokens(t.cacheRead)}\n${t.requests} requests` +
    (t.costApprox ? '\nIncludes models missing from the price table (estimated from their family).' : '');
  left.appendChild(big);
  left.appendChild(el('div', 'sub', `${fmt.tokens(fmt.total(t))} tokens  ·  ${t.requests} requests`));
  wrap.appendChild(left);

  const chart = el('div', 'chart');
  const bars = el('div', 'bars');
  const byCost = s.days.some((d) => d.tokens.cost > 0);
  const value = (d) => (byCost ? d.tokens.cost : fmt.total(d.tokens));
  const max = Math.max(1e-9, ...s.days.map(value));
  s.days.forEach((d, i) => {
    const day = el('div', `day${i === s.days.length - 1 ? ' today' : ''}`);
    const col = el('div', 'col');
    const v = value(d);
    const b = el('i', v > 0 ? '' : 'empty');
    b.style.height = `${v > 0 ? Math.max(4, (46 * v) / max) : 3}px`;
    col.appendChild(b);
    day.appendChild(col);
    day.appendChild(el('small', '', d.weekday[0]));
    day.title = `${d.weekday} ${d.date} · est. ${fmt.money(d.tokens.cost)} · ${fmt.tokens(fmt.total(d.tokens))} tokens`;
    bars.appendChild(day);
  });
  chart.appendChild(bars);
  chart.appendChild(el('div', 'week', `7 days  ${fmt.money(s.days.reduce((a, d) => a + d.tokens.cost, 0))}`));
  wrap.appendChild(chart);
  box.appendChild(wrap);
}

function renderCompactSummary(s) {
  const parts = [];
  if (s.today.cost > 0) parts.push(fmt.money(s.today.cost));
  if (s.limits && s.limits.fiveHour) parts.push(`5h ${Math.round(s.limits.fiveHour.usedPct)}%`);
  $('compact-summary').textContent = parts.join('  ·  ');
}

function makeRow() {
  const r = el('div', 'row');
  r.innerHTML = '<div class="st"></div><div class="main"><div class="l1"><span class="title"></span><span class="host"></span><span class="elapsed"></span></div>' +
    '<div class="l2"><span class="ico-slot"></span><span class="text"></span></div>' +
    '<div class="l3"><span class="project"></span><span class="ctxbar"></span><span class="ctx"></span><span class="meta"></span></div></div>';
  const q = (c) => r.querySelector(c);
  const parts = { st: q('.st'), title: q('.title'), host: q('.host'), elapsed: q('.elapsed'), icoSlot: q('.ico-slot'), text: q('.text'),
    project: q('.project'), ctxSlot: q('.ctxbar'), ctx: q('.ctx'), meta: q('.meta'), icon: null, bar: bar(0) };
  parts.ctxSlot.replaceWith(parts.bar);
  r.parts = parts;
  r.addEventListener('click', () => api.focus(r.pid));
  return r;
}

function setIcon(r, name) {
  if (r.parts.icon === name) return;
  r.parts.icon = name;
  r.parts.icoSlot.innerHTML = svg(name);
}

function updateRow(r, s, now) {
  const p = r.parts;
  r.pid = s.pid;
  if (r.state !== s.state) {
    r.state = s.state;
    r.className = `row ${s.state}`;
    p.st.className = `st ${s.state}`;
    p.st.innerHTML = s.state === 'done' ? svg('check', 10, 3.2) : '';
  }
  p.title.textContent = s.title;
  p.host.textContent = s.host;
  const dur = fmt.duration(now - s.statusSince);
  p.elapsed.textContent = s.state === 'done' ? `${dur} ago` : dur;

  const prompt = s.lastPrompt ? `“${fmt.oneLine(s.lastPrompt, 160)}”` : null;
  if (s.state === 'waiting') {
    setIcon(r, 'alert');
    p.text.textContent = `Needs you · ${s.waitingFor || 'waiting for input'}`;
  } else if (s.state === 'busy') {
    if (s.currentTool) {
      setIcon(r, toolIcon(s.currentTool.tool));
      let t = toolLabel(s.currentTool.tool);
      if (s.currentTool.detail) t += ` · ${s.currentTool.detail}`;
      if (s.runningTools > 1) t += `  (+${s.runningTools - 1})`;
      p.text.textContent = t;
    } else {
      setIcon(r, 'play');
      p.text.textContent = prompt || 'Working…';
    }
  } else {
    setIcon(r, s.state === 'done' ? 'check' : 'message');
    p.text.textContent = prompt || 'No prompts yet';
  }

  p.project.textContent = s.project;
  if (s.contextPct != null) {
    p.bar.style.display = '';
    setBar(p.bar, s.contextPct);
    p.ctx.textContent = `${s.contextExact ? '' : '~'}${Math.round(s.contextPct)}%`;
  } else {
    p.bar.style.display = 'none';
    p.ctx.textContent = '';
  }
  const meta = [];
  if (s.modelLabel) meta.push(s.modelLabel);
  const cost = s.tokens.cost + s.subagentTokens.cost;
  if (cost > 0) meta.push(fmt.money(cost));
  const tok = fmt.total(s.tokens) + fmt.total(s.subagentTokens);
  if (tok > 0) meta.push(`${fmt.tokens(tok)} tok`);
  p.meta.textContent = meta.length ? `${s.contextPct != null ? '  · ' : ''}${meta.join('  · ')}` : '';

  const tip = `${s.cwd}\n${s.host} · pid ${s.pid} · started ${fmt.ago(now - s.startedAt)}${s.version ? ` · Claude Code ${s.version}` : ''}\n` +
    (s.contextPct != null ? `Context: ${fmt.tokens(s.contextTokens)} of ${fmt.tokens(s.contextWindow)}\n` : '') +
    `Estimated cost: ${fmt.money(cost)} at API prices\n` +
    `Tokens: ${fmt.tokens(s.tokens.output + s.subagentTokens.output)} output, ${fmt.tokens(s.tokens.cacheRead + s.subagentTokens.cacheRead)} cache read, ` +
    `${s.tokens.requests + s.subagentTokens.requests} requests\nClick to bring its window to the front`;
  if (r.title !== tip) r.title = tip;
}

function renderSessions(s) {
  const box = $('sessions');
  const seen = new Set();
  const ordered = s.sessions.map((x) => {
    let r = rows.get(x.pid);
    if (!r) { r = makeRow(); rows.set(x.pid, r); }
    updateRow(r, x, s.generatedAt);
    seen.add(x.pid);
    return r;
  });
  for (const pid of [...rows.keys()]) if (!seen.has(pid)) { rows.get(pid).remove(); rows.delete(pid); }
  const same = box.children.length === ordered.length && ordered.every((r, i) => box.children[i] === r);
  if (!same) ordered.forEach((r) => box.appendChild(r)); // appendChild moves existing nodes
  $('empty').style.display = ordered.length ? 'none' : 'block';
}

function renderFooter(s) {
  const st = s.setup;
  const install = 'tray menu → “Install Claude Code hooks and status line”';
  let msg = null;
  if (s.platform === 'win32') msg = null;
  else if (!st.hooks && !st.statusLine) msg = `Live tool names and plan limits are off. Turn them on from the ${install}.`;
  else if (!st.hooks) msg = 'Hooks are not installed, so live tool names are off.';
  else if (!st.statusLine) msg = st.otherStatusLine ? 'You have your own status line, so plan limits are off.' : 'The status line is not installed, so plan limits are off.';
  const f = $('footer');
  f.textContent = msg || '';
  f.style.display = msg ? 'block' : 'none';
}

function render(s) {
  last = s;
  renderChips(s);
  renderLimits(s);
  renderUsage(s);
  renderCompactSummary(s);
  renderSessions(s);
  renderFooter(s);
  fit();
}

// ---------------------------------------------------------------- sizing

function updateMoreHint() {
  const l = $('list');
  $('more').style.display = l.scrollHeight - l.clientHeight - l.scrollTop > 4 ? 'block' : 'none';
}

/** Cap the session list to the room left on this monitor, then report the height. */
function fit() {
  const list = $('list');
  list.style.maxHeight = 'none';
  const card = $('card');
  const others = card.offsetHeight - list.offsetHeight;
  const max = Math.max(150, Math.min(620, room - others));
  list.style.maxHeight = `${max}px`;
  updateMoreHint();
  const h = Math.ceil(card.getBoundingClientRect().height);
  if (h !== sentHeight) { sentHeight = h; api.setHeight(h); }
}

api.onSnapshot(render);
api.onConfig((c) => {
  config = c;
  document.body.classList.toggle('compact', !!c.compact);
  $('btn-compact').innerHTML = svg(c.compact ? 'down' : 'up', 12, 2.4);
  if (last) render(last); else fit();
});
api.onLayout((l) => { room = l.room; fit(); });
let noticeTimer = null;
api.onNotice((text) => {
  const n = $('notice');
  n.textContent = text;
  n.style.display = 'block';
  fit();
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => { n.style.display = 'none'; fit(); }, 6000);
});
