const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  getConfig: () => ipcRenderer.invoke('get-config'),
  saveConfig: (data) => ipcRenderer.invoke('save-config', data),
  getConstants: () => ipcRenderer.invoke('get-constants'),
  detectScum: () => ipcRenderer.invoke('detect-scum'),
  findGameExe: (p) => ipcRenderer.invoke('find-game-exe', p),
  getModsPath: (p) => ipcRenderer.invoke('get-mods-path', p),
  fetchMods: () => ipcRenderer.invoke('fetch-mods'),
  scanMods: (p) => ipcRenderer.invoke('scan-mods', p),
  downloadAllMods: (mods, modsPath) => ipcRenderer.invoke('download-all-mods', mods, modsPath),
  deleteAllMods: (p) => ipcRenderer.invoke('delete-all-mods', p),
  launchGame: (opts) => ipcRenderer.invoke('launch-game', opts),
  openUrl: (url) => ipcRenderer.invoke('open-url', url),
  copyText: (text) => ipcRenderer.invoke('copy-text', text),
  browseFolder: (p) => ipcRenderer.invoke('browse-folder', p),
  onDownloadProgress: (cb) => {
    ipcRenderer.on('download-progress', (_, data) => cb(data));
  },
  minimize: () => ipcRenderer.send('window-minimize'),
  maximize: () => ipcRenderer.send('window-maximize'),
  close: () => ipcRenderer.send('window-close'),
  installUpdate: () => ipcRenderer.invoke('install-update'),
  onUpdateAvailable: (cb) => ipcRenderer.on('update-available', (_, v) => cb(v)),
  onUpdateProgress: (cb) => ipcRenderer.on('update-progress', (_, p) => cb(p)),
  onUpdateDownloaded: (cb) => ipcRenderer.on('update-downloaded', () => cb()),
});
