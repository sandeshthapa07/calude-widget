'use strict';
// Claude Widget for macOS and Linux: an always-on-top panel showing live Claude Code
// sessions, what each is doing, estimated cost and plan limits. (Windows has a native app.)
const { app, BrowserWindow, Tray, Menu, nativeImage, ipcMain, screen, Notification, shell } = require('electron');
const fs = require('fs');
const path = require('path');
const paths = require('./lib/paths');
const fmt = require('./lib/fmt');
const procs = require('./lib/procs');
const installer = require('./lib/installer');
const { Collector } = require('./lib/collector');

const BASE_WIDTH = 400;
const SIZES = [['Normal', 1.0], ['Large', 1.15], ['Extra large', 1.3]];
const isMac = process.platform === 'darwin';
const isLinux = process.platform === 'linux';

let win = null;
let tray = null;
let cfg = null;
let last = null;
let quitting = false;
let saveTimer = null;
let contentHeight = 300;
let firstSnapshot = true;
const lastStatus = new Map();
const busySince = new Map();

// ---------------------------------------------------------------- settings

function loadConfig() {
  const c = { x: null, y: null, scale: 1.0, compact: false, topmost: true, notify: true, ...(paths.readJson(paths.configFile()) || {}) };
  c.scale = Math.max(0.85, Math.min(1.6, Number(c.scale) || 1));
  return c;
}

function saveConfigSoon() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try { paths.writeAtomic(paths.configFile(), JSON.stringify(cfg, null, 2)); } catch (e) { paths.log('config', e); }
  }, 800);
}

// ---------------------------------------------------------------- window

function onSomeDisplay(x, y) {
  return screen.getAllDisplays().some(({ workArea: a }) => x >= a.x - 100 && x < a.x + a.width - 60 && y >= a.y - 20 && y < a.y + a.height - 60);
}

function width() { return Math.round(BASE_WIDTH * cfg.scale); }

function createWindow() {
  const wa = screen.getPrimaryDisplay().workArea;
  let x = wa.x + wa.width - width() - 12;
  let y = wa.y + 12;
  if (cfg.x != null && cfg.y != null && onSomeDisplay(cfg.x, cfg.y)) { x = cfg.x; y = cfg.y; }

  win = new BrowserWindow({
    x, y, width: width(), height: 320,
    frame: false, resizable: false, maximizable: false, minimizable: false, fullscreenable: false,
    skipTaskbar: true, alwaysOnTop: cfg.topmost, show: false, hasShadow: true,
    backgroundColor: '#121419', title: 'Claude Widget',
    // Opaque on purpose: transparent windows repaint in full on every frame.
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false, sandbox: true, backgroundThrottling: false },
  });
  applyTopmost();
  if (isMac) win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  win.loadFile(path.join(__dirname, 'ui', 'index.html'));
  win.webContents.on('did-finish-load', () => {
    win.webContents.setZoomFactor(cfg.scale);
    win.webContents.send('config', { compact: cfg.compact, platform: process.platform });
    sendLayout();
    if (last) win.webContents.send('snapshot', last);
  });
  win.once('ready-to-show', () => win.showInactive());
  win.on('moved', () => {
    const [nx, ny] = win.getPosition();
    cfg.x = nx; cfg.y = ny;
    saveConfigSoon();
    sendLayout();
  });
  win.on('close', (e) => { if (!quitting) { e.preventDefault(); win.hide(); } });
  screen.on('display-metrics-changed', sendLayout);
}

function applyTopmost() {
  if (!win) return;
  win.setAlwaysOnTop(cfg.topmost, isMac ? 'floating' : 'normal');
}

/** Tells the page how much vertical room is left on this monitor, so the session list
 *  scrolls instead of running off the bottom of the screen. */
function sendLayout() {
  if (!win) return;
  const b = win.getBounds();
  const wa = screen.getDisplayMatching(b).workArea;
  const room = (wa.y + wa.height - b.y - 8) / cfg.scale;
  win.webContents.send('layout', { room });
}

function setHeight(h) {
  contentHeight = h;
  const height = Math.max(80, Math.round(h * cfg.scale));
  const [w] = win.getContentSize();
  if (isLinux) win.setResizable(true); // some Linux window managers ignore resizes of fixed windows
  win.setContentSize(width(), height);
  if (isLinux) win.setResizable(false);
  if (w !== width()) sendLayout();
}

function setScale(scale) {
  cfg.scale = Math.max(0.85, Math.min(1.6, Math.round(scale * 100) / 100));
  win.webContents.setZoomFactor(cfg.scale);
  setHeight(contentHeight);
  sendLayout();
  saveConfigSoon();
  buildMenu();
}

function toggleWindow() {
  if (!win) return;
  if (win.isVisible()) win.hide(); else { win.showInactive(); win.focus(); }
}

function moveToCorner() {
  const wa = screen.getPrimaryDisplay().workArea;
  win.setPosition(wa.x + wa.width - width() - 12, wa.y + 12);
  win.showInactive();
}

// ---------------------------------------------------------------- start at login

function autostartFile() { return path.join(paths.home, '.config', 'autostart', 'claude-widget.desktop'); }

function startsAtLogin() {
  if (isLinux) return fs.existsSync(autostartFile());
  try { return app.getLoginItemSettings().openAtLogin; } catch { return false; }
}

function setStartsAtLogin(on) {
  if (!isLinux) { app.setLoginItemSettings({ openAtLogin: on }); return; }
  const f = autostartFile();
  if (!on) { try { fs.unlinkSync(f); } catch { /* not there */ } return; }
  const exec = process.env.APPIMAGE || process.execPath;
  const execLine = app.isPackaged ? `"${exec}"` : `"${exec}" "${app.getAppPath()}"`;
  fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(f, ['[Desktop Entry]', 'Type=Application', 'Name=Claude Widget', `Exec=${execLine}`,
    'X-GNOME-Autostart-enabled=true', 'Comment=Live Claude Code sessions, cost and plan limits', ''].join('\n'));
}

// ---------------------------------------------------------------- tray

function trayIcon() {
  if (isMac) {
    const img = nativeImage.createFromPath(path.join(__dirname, 'assets', 'trayTemplate.png'));
    img.setTemplateImage(true);
    return img;
  }
  return nativeImage.createFromPath(path.join(__dirname, 'assets', 'tray.png'));
}

function buildMenu() {
  if (!tray) return;
  const menu = Menu.buildFromTemplate([
    { label: 'Show / hide', click: toggleWindow },
    { label: 'Text size', submenu: SIZES.map(([name, s]) => ({ label: name, type: 'radio', checked: Math.abs(cfg.scale - s) < 0.01, click: () => setScale(s) })) },
    { label: 'Compact view', type: 'checkbox', checked: cfg.compact, click: toggleCompact },
    { label: 'Always on top', type: 'checkbox', checked: cfg.topmost, click: () => { cfg.topmost = !cfg.topmost; applyTopmost(); saveConfigSoon(); } },
    { label: 'Notify when a session finishes or needs me', type: 'checkbox', checked: cfg.notify, click: () => { cfg.notify = !cfg.notify; saveConfigSoon(); } },
    { label: isMac ? 'Open at login' : 'Start at login', type: 'checkbox', checked: startsAtLogin(), click: (item) => { try { setStartsAtLogin(item.checked); } catch (e) { paths.log('autostart', e); } } },
    { label: 'Move to top-right corner', click: moveToCorner },
    { type: 'separator' },
    last && last.setup && last.setup.hooks
      ? { label: 'Remove Claude Code hooks and status line', click: () => runInstaller(false) }
      : { label: 'Install Claude Code hooks and status line…', click: () => runInstaller(true) },
    { label: 'Open widget data folder', click: () => shell.openPath(paths.widgetDir()) },
    { type: 'separator' },
    { label: 'Quit Claude Widget', click: () => { quitting = true; app.quit(); } },
  ]);
  if (isMac) {
    // Left click toggles the panel, right click opens the menu.
    tray.removeAllListeners('right-click');
    tray.on('right-click', () => tray.popUpContextMenu(menu));
  } else {
    tray.setContextMenu(menu); // Linux app indicators only support a menu
  }
}

function createTray() {
  tray = new Tray(trayIcon());
  tray.setToolTip('Claude Widget');
  if (isMac) tray.on('click', toggleWindow);
  buildMenu();
}

function runInstaller(install) {
  const lines = [];
  const ok = install ? installer.install((l) => lines.push(l)) : installer.uninstall((l) => lines.push(l));
  new Notification({ title: ok ? (install ? 'Hooks installed' : 'Hooks removed') : 'Claude Widget', body: lines.slice(-2).join('\n') }).show();
  setTimeout(buildMenu, 11000); // the collector re-reads settings every 10 s
}

function toggleCompact() {
  cfg.compact = !cfg.compact;
  saveConfigSoon();
  win.webContents.send('config', { compact: cfg.compact, platform: process.platform });
  buildMenu();
}

// ---------------------------------------------------------------- live data

function countsLine(s) {
  const c = s.counts;
  const parts = [];
  if (c.waiting) parts.push(`${c.waiting} waiting`);
  if (c.busy) parts.push(`${c.busy} busy`);
  if (c.done) parts.push(`${c.done} done`);
  if (c.idle) parts.push(`${c.idle} idle`);
  return parts.length ? parts.join(' · ') : 'no sessions';
}

function notify(s, title, body) {
  if (!Notification.isSupported()) return;
  const n = new Notification({ title, body });
  n.on('click', () => procs.focusSession(s));
  n.show();
}

function checkTransitions(snap) {
  const now = snap.generatedAt;
  const current = new Map();
  for (const s of snap.sessions) {
    current.set(s.pid, s.status);
    const prev = lastStatus.get(s.pid);
    const working = s.status !== 'idle';
    if (working && !busySince.has(s.pid)) busySince.set(s.pid, firstSnapshot ? s.statusSince : now);
    if (!firstSnapshot && prev !== undefined && prev !== s.status && cfg.notify) {
      if (s.status === 'waiting' && s.waitingFor !== 'dialog open') {
        const why = s.waitingFor || 'waiting for input';
        notify(s, `Needs you · ${s.title}`, `${why[0].toUpperCase()}${why.slice(1)} — ${s.host} · ${s.project}`);
      } else if (s.status === 'idle' && busySince.has(s.pid) && now - busySince.get(s.pid) >= 20000) {
        notify(s, `Done · ${s.title}`, `Finished after ${fmt.duration(now - busySince.get(s.pid))} — ${s.host} · ${s.project}`);
      }
    }
    if (!working) busySince.delete(s.pid);
  }
  lastStatus.clear();
  for (const [k, v] of current) lastStatus.set(k, v);
  for (const k of [...busySince.keys()]) if (!current.has(k)) busySince.delete(k);
  firstSnapshot = false;
}

function startLoop() {
  const collector = new Collector();
  let hadHooks = null;
  const tick = () => {
    try {
      const snap = collector.collect();
      last = snap;
      if (win && !win.isDestroyed()) win.webContents.send('snapshot', snap);
      checkTransitions(snap);
      const cost = snap.today.cost > 0 ? ` · ${fmt.money(snap.today.cost)} today` : '';
      tray.setToolTip(`Claude · ${countsLine(snap)}${cost}`);
      if (isMac) {
        // Menu bar text next to the icon: what needs attention.
        const c = snap.counts;
        tray.setTitle(c.waiting ? ` ${c.waiting} waiting` : c.busy ? ` ${c.busy} busy` : '');
      }
      if (hadHooks !== snap.setup.hooks) { hadHooks = snap.setup.hooks; buildMenu(); }
    } catch (e) { paths.log('collect', e); }
    setTimeout(tick, 1000);
  };
  tick();
}

// ---------------------------------------------------------------- app

ipcMain.on('set-height', (_e, h) => { if (win && Number.isFinite(h)) setHeight(h); });
ipcMain.on('focus', async (e, pid) => {
  const s = last && last.sessions.find((x) => x.pid === pid);
  if (!s) return;
  const ok = await procs.focusSession(s);
  if (!ok && !e.sender.isDestroyed()) {
    e.sender.send('notice', isLinux
      ? 'Could not switch to that window. Install wmctrl (works on X11 and XWayland apps).'
      : 'Could not switch to that window.');
  }
});
ipcMain.on('toggle-compact', toggleCompact);
ipcMain.on('hide', () => win && win.hide());
ipcMain.on('zoom', (_e, delta) => setScale(cfg.scale + delta));

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => { if (win) { win.showInactive(); win.focus(); } });
  app.on('window-all-closed', () => { /* keep running in the tray */ });
  app.on('before-quit', () => { quitting = true; });
  app.whenReady().then(() => {
    if (isMac && app.dock) app.dock.hide(); // menu bar app, no Dock icon
    cfg = loadConfig();
    createWindow();
    createTray();
    startLoop();
  });
}
