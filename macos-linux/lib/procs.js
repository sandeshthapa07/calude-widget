'use strict';
// Process checks, "which app hosts this session", and bringing that app's window forward.
// macOS and Linux. (On Windows the native C# widget does this; here Windows only gets the
// basics, which is enough to run this app there for development.)
const { execFile, execFileSync } = require('child_process');
const path = require('path');

const platform = process.platform;

function isAlive(pid) {
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

/** Claude Code stores `ps -o lstart= -p <pid>` (LC_ALL=C, TZ=UTC) as procStart on macOS and
 *  Linux; comparing it guards against a crashed session's pid being reused. */
function lstart(pid) {
  try {
    const out = execFileSync('ps', ['-o', 'lstart=', '-p', String(pid)], {
      encoding: 'utf8', timeout: 1500, env: { ...process.env, LC_ALL: 'C', TZ: 'UTC' }, stdio: ['ignore', 'pipe', 'ignore'],
    });
    return out.trim() || null;
  } catch { return null; }
}

const verified = new Map(); // `${pid}:${procStart}` -> { ok, at }

function isSameProcess(pid, procStart) {
  if (!isAlive(pid)) return false;
  if (!procStart || platform === 'win32') return true; // Windows stores a FILETIME; the C# app checks it
  const key = `${pid}:${procStart}`;
  const hit = verified.get(key);
  if (hit && Date.now() - hit.at < 30000) return hit.ok;
  const actual = lstart(pid);
  const ok = actual === null ? true : actual === String(procStart).trim();
  verified.set(key, { ok, at: Date.now() });
  if (verified.size > 200) verified.delete(verified.keys().next().value);
  return ok;
}

/** pid -> { ppid, comm } for all processes (one `ps` call). Null on Windows. */
function processTable() {
  if (platform === 'win32') return null;
  try {
    const out = execFileSync('ps', ['-A', '-o', 'pid=,ppid=,comm='], {
      encoding: 'utf8', timeout: 3000, maxBuffer: 32 << 20, stdio: ['ignore', 'pipe', 'ignore'],
    });
    const map = new Map();
    for (const line of out.split('\n')) {
      const m = /^\s*(\d+)\s+(\d+)\s+(.*)$/.exec(line);
      if (m) map.set(Number(m[1]), { ppid: Number(m[2]), comm: m[3].trim() });
    }
    return map;
  } catch { return null; }
}

function ancestors(pid, table) {
  const out = [];
  const seen = new Set([pid]);
  let cur = pid;
  while (table && table.has(cur) && out.length < 16) {
    const parent = table.get(cur).ppid;
    if (!parent || seen.has(parent) || !table.has(parent)) break;
    seen.add(parent);
    out.push(parent);
    cur = parent;
  }
  return out;
}

/** "/Applications/Cursor.app/Contents/Frameworks/Cursor Helper (Plugin).app/..." -> "Cursor"
 *  (first .app in the path), else the executable name. */
function appName(comm) {
  const m = /\/([^/]+)\.app\//.exec(comm);
  return m ? m[1] : path.basename(comm);
}

const HOSTS = [
  [/^cursor/i, 'Cursor'], [/^(code|visual studio code|code - insiders|code-insiders|vscodium|codium)$/i, 'VS Code'],
  [/^windsurf/i, 'Windsurf'], [/^zed/i, 'Zed'],
  [/^(intellij|idea|pycharm|webstorm|goland|rider|clion|phpstorm|rubymine|android studio)/i, 'JetBrains'],
  [/^iterm/i, 'iTerm'], [/^terminal$/i, 'Terminal'], [/^warp|^stable$/i, 'Warp'], [/^ghostty/i, 'Ghostty'],
  [/^kitty$/i, 'kitty'], [/^alacritty$/i, 'Alacritty'], [/^wezterm/i, 'WezTerm'], [/^hyper$/i, 'Hyper'], [/^tabby$/i, 'Tabby'],
  [/^gnome-terminal/i, 'Terminal'], [/^(kgx|gnome-console)$/i, 'Console'], [/^ptyxis/i, 'Terminal'], [/^konsole$/i, 'Konsole'],
  [/^xfce4-terminal$/i, 'Terminal'], [/^tilix$/i, 'Tilix'], [/^terminator$/i, 'Terminator'], [/^foot$/i, 'foot'],
  [/^xterm$/i, 'xterm'], [/^tmux/i, 'tmux'], [/^zellij$/i, 'zellij'], [/^screen$/i, 'screen'],
];

function hostFor(comm) {
  const name = appName(comm);
  for (const [rx, label] of HOSTS) if (rx.test(name)) return label;
  return null;
}

/** Friendly name of the app hosting a session ("Cursor", "iTerm", "Terminal", ...). */
function hostLabel(pid, entrypoint, table) {
  for (const a of ancestors(pid, table)) {
    const label = hostFor(table.get(a).comm);
    if (label) return label;
  }
  if (entrypoint === 'cli') return 'Terminal';
  if (entrypoint === 'claude-vscode') return 'IDE';
  if (!entrypoint) return 'Claude';
  if (entrypoint.startsWith('sdk')) return 'SDK';
  return entrypoint;
}

function run(cmd, args, timeout = 4000) {
  return new Promise((resolve) => {
    execFile(cmd, args, { timeout }, (err, stdout) => resolve(err ? null : String(stdout)));
  });
}

const asString = (s) => `"${String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;

/** macOS: Terminal and iTerm2 tabs are matched by the session's tty (precise). IDE windows by
 *  folder name in the window title (needs Accessibility permission; otherwise the app is just
 *  activated). Other apps are activated. */
async function focusMac(s, table) {
  const chain = ancestors(s.pid, table);
  const appComm = chain.map((p) => table.get(p).comm).find((c) => c.includes('.app/'));
  if (!appComm) return false;
  const appPath = appComm.slice(0, appComm.indexOf('.app/') + 4);
  const app = appName(appComm);
  const ttyOut = await run('ps', ['-o', 'tty=', '-p', String(s.pid)]);
  const tty = ttyOut && ttyOut.trim() && ttyOut.trim() !== '??' ? `/dev/${ttyOut.trim()}` : null;
  const folder = path.basename(s.cwd || '');

  let script = null;
  if (app === 'Terminal' && tty) {
    script = `tell application "Terminal"
      repeat with w in windows
        repeat with t in tabs of w
          if tty of t is ${asString(tty)} then
            set selected tab of w to t
            set index of w to 1
          end if
        end repeat
      end repeat
      activate
    end tell`;
  } else if (/^iTerm/.test(app) && tty) {
    script = `tell application "iTerm2"
      repeat with w in windows
        repeat with t in tabs of w
          repeat with ss in sessions of t
            if tty of ss is ${asString(tty)} then
              tell w to select
              tell t to select
              tell ss to select
            end if
          end repeat
        end repeat
      end repeat
      activate
    end tell`;
  } else if (folder) {
    script = `tell application ${asString(app)} to activate
      try
        tell application "System Events" to tell process ${asString(app)}
          perform action "AXRaise" of (first window whose name contains ${asString(folder)})
        end tell
      end try`;
  }
  if (script && (await run('osascript', ['-e', script])) !== null) return true;
  return (await run('open', ['-a', appPath])) !== null;
}

/** Linux: needs wmctrl (X11 / XWayland windows). IDE windows match the folder in the title,
 *  terminals match Claude's AI title (Claude sets the terminal title). */
async function focusLinux(s, table) {
  const list = await run('wmctrl', ['-lp']);
  if (list === null) return false;
  const wins = list.split('\n').map((l) => /^(0x[0-9a-f]+)\s+\S+\s+(\d+)\s+\S+\s+(.*)$/i.exec(l)).filter(Boolean)
    .map((m) => ({ id: m[1], pid: Number(m[2]), title: m[3] }));
  const chain = ancestors(s.pid, table);
  const folder = path.basename(s.cwd || '').toLowerCase();
  const hints = [s.aiTitle, s.title].filter((h) => h && h.length >= 3).map((h) => h.toLowerCase());
  const ide = ['Cursor', 'VS Code', 'Windsurf', 'Zed'].includes(s.host);
  const pick = (ide && folder && wins.find((w) => w.title.toLowerCase().split(' - ').some((p) => p.trim() === folder)))
    || wins.find((w) => hints.some((h) => w.title.toLowerCase().includes(h)))
    || wins.find((w) => chain.includes(w.pid));
  if (!pick) return false;
  return (await run('wmctrl', ['-ia', pick.id])) !== null;
}

async function focusSession(s) {
  try {
    const table = processTable();
    if (!table) return false;
    if (platform === 'darwin') return await focusMac(s, table);
    if (platform === 'linux') return await focusLinux(s, table);
  } catch { /* fall through */ }
  return false;
}

module.exports = { isAlive, isSameProcess, processTable, ancestors, hostLabel, focusSession, lstart, appName, hostFor };
