#!/usr/bin/env node
'use strict';
// Command line companion: what the widget sees, plus install / uninstall of the hooks.
//   node cli.js snapshot [--json]
//   node cli.js watch [seconds]
//   node cli.js install | uninstall
const fmt = require('./lib/fmt');
const live = require('./lib/live');
const { Collector, total } = require('./lib/collector');
const installer = require('./lib/installer');

const [cmd = '', arg] = process.argv.slice(2);

function snapshot(asJson) {
  const started = Date.now();
  const snap = new Collector().collect();
  if (asJson) { console.log(JSON.stringify(snap, null, 2)); return; }
  const c = snap.counts;
  console.log(`Sessions: ${c.waiting} waiting, ${c.busy} busy, ${c.done} done recently, ${c.idle} idle   (collected in ${Date.now() - started} ms)`);
  for (const s of snap.sessions) {
    console.log(`\n  [${s.state.toUpperCase()}] ${s.title}`);
    console.log(`      ${s.host} · ${s.project} · pid ${s.pid} · ${s.status} for ${fmt.duration(snap.generatedAt - s.statusSince)}`);
    if (s.waitingFor) console.log(`      waiting for: ${s.waitingFor}`);
    if (s.currentTool) console.log(`      running: ${live.label(s.currentTool.tool)} ${s.currentTool.detail}${s.runningTools > 1 ? ` (+${s.runningTools - 1} more)` : ''}`);
    const pct = s.contextPct == null ? '' : ` (${s.contextExact ? '' : '~'}${Math.round(s.contextPct)}%)`;
    console.log(`      ${s.modelLabel || 'model ?'} · context ${s.contextTokens >= 0 ? fmt.tokens(s.contextTokens) : '?'} of ${s.contextWindow ? fmt.tokens(s.contextWindow) : '?'}${pct}` +
      ` · tokens ${fmt.tokens(total(s.tokens))} (+${fmt.tokens(total(s.subagentTokens))} in subagents) in ${s.tokens.requests + s.subagentTokens.requests} requests` +
      ` · est. cost ${fmt.money(s.tokens.cost + s.subagentTokens.cost)}`);
    if (s.lastPrompt) console.log(`      last prompt: ${fmt.oneLine(s.lastPrompt, 100)}`);
  }
  const t = snap.today;
  console.log(`\nToday: est. ${fmt.money(t.cost)} · ${fmt.tokens(total(t))} tokens in ${t.requests} requests (input ${fmt.tokens(t.input)}, output ${fmt.tokens(t.output)}, cache write ${fmt.tokens(t.cacheWrite)}, cache read ${fmt.tokens(t.cacheRead)})`);
  console.log(`Last 7 days: est. ${fmt.money(snap.days.reduce((a, d) => a + d.tokens.cost, 0))} · ${snap.days.map((d) => `${d.weekday} ${fmt.money(d.tokens.cost)}`).join('  ')}`);
  console.log(`Prices: ${snap.priceSource}`);
  const L = snap.limits;
  if (L) {
    if (L.fiveHour) console.log(`5-hour limit: ${Math.round(L.fiveHour.usedPct)}% used, resets ${fmt.resetsIn(L.fiveHour.resetsAt, snap.generatedAt)}`);
    if (L.sevenDay) console.log(`Weekly limit: ${Math.round(L.sevenDay.usedPct)}% used, resets ${fmt.resetsIn(L.sevenDay.resetsAt, snap.generatedAt)}`);
    console.log(`(limits captured ${fmt.ago(snap.generatedAt - L.capturedAt)})`);
  } else console.log('Plan limits: none captured yet (they come from the status line)');
  const st = snap.setup;
  console.log(`Setup: hooks ${st.hooks ? 'installed' : 'not installed'}, status line ${st.statusLine ? 'installed' : st.otherStatusLine ? `custom (${st.otherStatusLine})` : 'not installed'}`);
}

function watch(seconds) {
  const collector = new Collector();
  const last = new Map();
  const end = Date.now() + seconds * 1000;
  const tick = () => {
    const snap = collector.collect();
    const seen = new Set();
    for (const s of snap.sessions) {
      seen.add(s.pid);
      const line = `${s.state} | ${s.title} | prompt: ${s.lastPrompt ? fmt.oneLine(s.lastPrompt, 50) : '-'}`;
      if (last.get(s.pid) !== line) { console.log(`${new Date().toTimeString().slice(0, 8)} pid ${String(s.pid).padEnd(6)} ${line}`); last.set(s.pid, line); }
    }
    for (const pid of [...last.keys()]) if (!seen.has(pid)) { console.log(`${new Date().toTimeString().slice(0, 8)} pid ${String(pid).padEnd(6)} gone`); last.delete(pid); }
    if (Date.now() < end) setTimeout(tick, 1000);
  };
  tick();
}

switch (cmd) {
  case 'snapshot': snapshot(arg === '--json'); break;
  case 'watch': watch(Number(arg) || 60); break;
  case 'install': process.exitCode = installer.install() ? 0 : 1; break;
  case 'uninstall': process.exitCode = installer.uninstall() ? 0 : 1; break;
  default:
    console.log('node cli.js snapshot [--json]   show what the widget sees right now');
    console.log('node cli.js watch [seconds]     keep a collector running and print every change');
    console.log('node cli.js install             add the hooks + status line to ~/.claude/settings.json');
    console.log('node cli.js uninstall           remove them again');
}
