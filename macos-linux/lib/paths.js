'use strict';
// Where Claude Code keeps its state, and where the widget keeps its own files.
const fs = require('fs');
const os = require('os');
const path = require('path');

const home = os.homedir();

function claudeDir() {
  const env = process.env.CLAUDE_CONFIG_DIR;
  return env && env.trim() ? env : path.join(home, '.claude');
}

const p = {
  home,
  claudeDir,
  sessionsDir: () => path.join(claudeDir(), 'sessions'),
  projectsDir: () => path.join(claudeDir(), 'projects'),
  settingsFile: () => path.join(claudeDir(), 'settings.json'),
  /** Everything the widget writes: ~/.claude/widget (shared with the Windows app). */
  widgetDir: () => path.join(claudeDir(), 'widget'),
  liveDir: () => path.join(claudeDir(), 'widget', 'live'),
  statuslineDir: () => path.join(claudeDir(), 'widget', 'statusline'),
  limitsFile: () => path.join(claudeDir(), 'widget', 'limits.json'),
  modelsFile: () => path.join(claudeDir(), 'widget', 'models.json'),
  pricesFile: () => path.join(claudeDir(), 'widget', 'prices.json'),
  configFile: () => path.join(claudeDir(), 'widget', 'app-config.json'),
  logFile: () => path.join(claudeDir(), 'widget', 'app.log'),
  toolsDir: () => path.join(claudeDir(), 'tools', 'claude-widget'),
  installedHook: () => path.join(claudeDir(), 'tools', 'claude-widget', 'claude-widget-hook.sh'),
};

p.readText = (file) => {
  try { return fs.readFileSync(file, 'utf8'); } catch { return null; }
};

p.readJson = (file) => {
  const t = p.readText(file);
  if (t == null) return null;
  try { return JSON.parse(t.replace(/^﻿/, '')); } catch { return null; }
};

/** Write via temp file + rename so readers never see half a file. */
p.writeAtomic = (file, text) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp${process.pid}`;
  fs.writeFileSync(tmp, text);
  try { fs.renameSync(tmp, file); } catch (e) {
    try { fs.writeFileSync(file, text); } finally { try { fs.unlinkSync(tmp); } catch { /* gone */ } }
  }
};

p.log = (where, err) => {
  try {
    fs.mkdirSync(p.widgetDir(), { recursive: true });
    const f = p.logFile();
    try { if (fs.statSync(f).size > 512 * 1024) fs.unlinkSync(f); } catch { /* no log yet */ }
    fs.appendFileSync(f, `${new Date().toISOString()} [${where}] ${err && err.stack ? err.stack : err}\n`);
  } catch { /* logging must never throw */ }
};

module.exports = p;
