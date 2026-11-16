'use strict';
// Running-tool markers written by the hooks: ~/.claude/widget/live/<session>/<tool_use_id>.run
// (tool started) and .done (finished). The macOS/Linux shell hook stores Claude's raw hook
// JSON in the .run file; the Windows hook stores {tool, detail, since}. Both are read here.
const fs = require('fs');
const path = require('path');
const paths = require('./paths');
const fmt = require('./fmt');

function safe(s) { return String(s || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 100); }

/** "mcp__windows-mcp__Click" -> "windows-mcp · Click" */
function label(tool) {
  if (tool && tool.startsWith('mcp__')) {
    const rest = tool.slice(5);
    const sep = rest.indexOf('__');
    if (sep > 0) return `${rest.slice(0, sep)} · ${rest.slice(sep + 2)}`;
  }
  return tool || '';
}

/** One-line summary of what a tool call is doing. */
function describe(tool, input) {
  input = input || {};
  const s = (k) => (typeof input[k] === 'string' ? input[k] : null);
  const base = (p) => (p ? path.basename(p.replace(/[\\/]+$/, '')) : null);
  let d;
  switch (tool) {
    case 'Bash': case 'PowerShell': d = s('description') || s('command'); break;
    case 'Read': case 'Write': case 'Edit': case 'MultiEdit': d = base(s('file_path')); break;
    case 'NotebookEdit': d = base(s('notebook_path')); break;
    case 'Grep': case 'Glob': d = s('pattern'); break;
    case 'WebFetch': d = s('url'); break;
    case 'WebSearch': d = s('query'); break;
    case 'Agent': case 'Task': d = s('description'); break;
    case 'Skill': d = s('skill'); break;
    default: d = Object.values(input).find((v) => typeof v === 'string' && v.trim()) || null;
  }
  return fmt.oneLine((d || '').split('\n')[0], 90);
}

/** Running tools in a session, newest first. Markers older than 30 minutes are leftovers. */
function running(sessionId) {
  const id = safe(sessionId);
  if (!id) return [];
  const dir = path.join(paths.liveDir(), id);
  let names;
  try { names = fs.readdirSync(dir).filter((n) => n.endsWith('.run')); } catch { return []; }
  const cutoff = Date.now() - 30 * 60 * 1000;
  const out = [];
  for (const n of names) {
    const file = path.join(dir, n);
    let st;
    try { st = fs.statSync(file); } catch { continue; }
    const o = paths.readJson(file);
    if (!o) continue;
    let run;
    if (o.tool_name !== undefined) run = { tool: o.tool_name, detail: describe(o.tool_name, o.tool_input), since: st.mtimeMs };
    else run = { tool: o.tool || '', detail: o.detail || '', since: o.since || st.mtimeMs };
    if (run.since >= cutoff) out.push(run);
  }
  return out.sort((a, b) => b.since - a.since);
}

module.exports = { running, label, describe, safe };
