'use strict';
// Adds/removes the widget's hooks and status line in ~/.claude/settings.json (macOS/Linux),
// next to whatever is already there. A backup of the pre-install file is written once.
const fs = require('fs');
const path = require('path');
const paths = require('./paths');

const MARKER = 'claude-widget-hook';
const TOOL_EVENTS = ['PreToolUse', 'PostToolUse', 'PostToolUseFailure'];
const TURN_EVENTS = ['UserPromptSubmit', 'Stop', 'SessionEnd'];

const isOurs = (group) => !!group && Array.isArray(group.hooks) && group.hooks.some((h) => JSON.stringify(h).includes(MARKER));

function shQuote(s) { return /^[A-Za-z0-9_\/.~-]+$/.test(s) ? s : `'${s.replace(/'/g, `'\\''`)}'`; }

function hookSource() { return path.join(__dirname, '..', 'hook', 'claude-widget-hook.sh'); }

function editSettings(uninstall, log) {
  const file = paths.settingsFile();
  let root = {};
  let crlf = false;
  if (fs.existsSync(file)) {
    const original = fs.readFileSync(file, 'utf8');
    crlf = original.includes('\r\n');
    try { root = JSON.parse(original.replace(/^﻿/, '')); } catch {
      log(`error: ${file} is not valid JSON; not touching it.`);
      return false;
    }
    if (!root || typeof root !== 'object' || Array.isArray(root)) { log(`error: ${file} is not a JSON object.`); return false; }
    const backup = `${file}.bak-claude-widget`;
    if (!original.includes(MARKER)) { fs.copyFileSync(file, backup); log(`backed up settings to ${backup}`); }
    else if (fs.existsSync(backup)) log(`keeping the earlier backup ${backup}`);
  }

  const script = paths.installedHook();
  if (!root.hooks || typeof root.hooks !== 'object') root.hooks = {};
  for (const ev of [...TOOL_EVENTS, ...TURN_EVENTS]) {
    const kept = (Array.isArray(root.hooks[ev]) ? root.hooks[ev] : []).filter((g) => !isOurs(g));
    if (!uninstall) {
      kept.push({ hooks: [{ type: 'command', command: '/bin/sh', args: [script, 'hook'], timeout: 5, async: true }] });
    }
    if (kept.length) root.hooks[ev] = kept; else delete root.hooks[ev];
  }
  if (!Object.keys(root.hooks).length) delete root.hooks;

  const sl = root.statusLine;
  const slOurs = !!(sl && typeof sl.command === 'string' && sl.command.includes(MARKER));
  if (!uninstall) {
    if (!sl || slOurs) {
      root.statusLine = { type: 'command', command: `sh ${shQuote(script)} statusline`, padding: 0 };
      log('status line: set to the widget\'s (shows model, context and plan limits)');
    } else {
      log(`status line: kept your existing one (${sl.command}); plan limits stay off.`);
    }
  } else if (slOurs) {
    delete root.statusLine;
    log('status line: removed');
  }

  let text = `${JSON.stringify(root, null, 2)}\n`;
  if (crlf) text = text.replace(/\n/g, '\r\n');
  paths.writeAtomic(file, text);
  log(`${uninstall ? 'removed' : 'added'} hooks for: ${[...TOOL_EVENTS, ...TURN_EVENTS].join(', ')}`);
  return true;
}

function install(log = console.log) {
  if (process.platform === 'win32') {
    log('On Windows use the native widget: bin\\claude-widget-cli.exe install');
    return false;
  }
  fs.mkdirSync(paths.toolsDir(), { recursive: true });
  const target = paths.installedHook();
  // Normalise line endings: a CRLF shell script fails on macOS/Linux.
  fs.writeFileSync(target, fs.readFileSync(hookSource(), 'utf8').replace(/\r\n/g, '\n'));
  fs.chmodSync(target, 0o755);
  log(`installed hook script: ${target}`);
  return editSettings(false, log);
}

function uninstall(log = console.log) {
  if (process.platform === 'win32') {
    log('On Windows use the native widget: bin\\claude-widget-cli.exe uninstall');
    return false;
  }
  const ok = editSettings(true, log);
  log(`left in place (delete by hand if you like): ${paths.toolsDir()} and ${paths.widgetDir()}`);
  return ok;
}

module.exports = { install, uninstall, isOurs };
