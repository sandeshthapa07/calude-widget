# Claude Widget for macOS and Linux

This is the same widget as the Windows app (`../`), built with Electron. It's an always-on-top panel that shows:

- every running Claude Code session: busy / needs you / done / idle, with what each is doing
- estimated cost, today's tokens and a 7-day chart
- plan limits (5-hour and weekly)

Click a session to bring its window to the front. You get a notification when a session finishes or needs you.

It reads the same files Claude Code writes on every OS (`~/.claude/sessions`, the transcripts and the price catalog), so the numbers match the Windows app exactly.

## Install

### macOS

```sh
git clone https://github.com/sandeshthapa07/calude-widget.git
cd calude-widget/macos-linux
npm install
npm start                # run from source
npm run dist:mac         # or build "Claude Widget.dmg" / .zip into dist/
```

- **Menu bar, no Dock icon.** Click the menu bar icon to show or hide the panel, and right-click it for the menu. The menu bar text shows "1 waiting" or "2 busy" when something is going on.
- **Unsigned build.** The first time, right-click the app → **Open** (or run `xattr -dr com.apple.quarantine "/Applications/Claude Widget.app"`).
- **Clicking a session** selects the exact tab in **Terminal** or **iTerm2** (matched by tty). macOS asks once to let the widget control that app. For Cursor and VS Code it activates the app. If you grant **Accessibility** permission, it raises the window for that folder.

### Linux

```sh
git clone https://github.com/sandeshthapa07/calude-widget.git
cd calude-widget/macos-linux
npm install
npm start                    # run from source, or build packages:
npm run dist:linux           # -> dist/claude-widget_<version>_amd64.deb and dist/Claude Widget-<version>.AppImage
sudo apt install ./dist/claude-widget_*_amd64.deb                          # Debian / Ubuntu
chmod +x dist/*.AppImage && ./dist/Claude\ Widget-*.AppImage               # any distro
```

- **Tray icon:** it needs an AppIndicator host. Ubuntu has one built in. On plain GNOME, install the "AppIndicator and KStatusNotifierItem Support" extension. The panel works without it.
- **Clicking a session** needs `wmctrl` (`sudo apt install wmctrl`). It works for X11 and XWayland windows. Native Wayland windows can't be raised by other apps.
- **AppImage:** needs `libfuse2`, or set `APPIMAGE_EXTRACT_AND_RUN=1`.

### Turn on live tool names and plan limits

Use the tray / menu bar menu → **Install Claude Code hooks and status line**, or run `node cli.js install`.

This adds async hooks and a status line to `~/.claude/settings.json`, next to your existing entries. The pre-install file is saved once as `settings.json.bak-claude-widget`. To undo it, use the menu's **Remove…** item or run `node cli.js uninstall`.

## How it differs from the Windows version

- **The hook is a POSIX shell script** (`hook/claude-widget-hook.sh`, copied to `~/.claude/tools/claude-widget/`). It needs no jq, Node or Python, so it works with Claude Code's native installer. It only saves what Claude sends; the app does the parsing.
- **The status line text** (`Opus 5.5 · ctx 34% · 5h 16% · 7d 12%`) is prepared by the app. Until the app has run once, the status line shows just the model name.
- **Process checks** use `ps -o lstart=`, the same value Claude Code stores as `procStart`. That way a crashed session whose pid was reused is never shown as live.
- **Memory:** Electron uses about 150 MB. The native Windows app uses about 75 MB.

## What was tested

- **Linux** (Ubuntu 26.04 under WSLg), 25 automated checks:
  - session detection with `ps` (stale and dead processes ignored)
  - host detection from the process tree
  - the cost formula
  - install and uninstall (other hooks and settings kept, backup written)
  - every hook event and the status line
- **Linux GUI:** the panel ran and was screenshotted, both from source and as the AppImage. The `.deb` and the packaged app's own installer were also checked.
- **Windows** (development only): numbers matched the native app exactly (tokens, cost, limits, titles), and reading the price catalog from the Claude binary was tested.
- **macOS: not tested.** I didn't have a Mac to test on. The Mac-specific parts are the `ps` calls, `osascript` for focusing Terminal/iTerm2/IDE windows, the menu bar template icon, login items and the `.dmg` build. If something misbehaves, `node cli.js snapshot` shows what the widget sees.

## Commands

```sh
node cli.js snapshot [--json]   # what the widget sees right now
node cli.js watch [seconds]     # print every change in state, title or prompt
node cli.js install | uninstall # hooks + status line
```

Runtime data is shared with the Windows app in `~/.claude/widget/`. App settings, such as position, size and compact view, are in `app-config.json`. Errors are logged to `app.log`.
