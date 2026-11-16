'use strict';
// The only bridge between the page and the main process.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('widget', {
  onSnapshot: (cb) => ipcRenderer.on('snapshot', (_e, s) => cb(s)),
  onConfig: (cb) => ipcRenderer.on('config', (_e, c) => cb(c)),
  onLayout: (cb) => ipcRenderer.on('layout', (_e, l) => cb(l)),
  onNotice: (cb) => ipcRenderer.on('notice', (_e, text) => cb(text)),
  setHeight: (h) => ipcRenderer.send('set-height', h),
  focus: (pid) => ipcRenderer.send('focus', pid),
  toggleCompact: () => ipcRenderer.send('toggle-compact'),
  hide: () => ipcRenderer.send('hide'),
  zoom: (delta) => ipcRenderer.send('zoom', delta),
});
