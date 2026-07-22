const { app, BrowserWindow, ipcMain, dialog, shell, clipboard } = require('electron');
const path = require('path');
const fs = require('fs');
const https = require('https');
const http = require('http');
const { spawn } = require('child_process');
const { Client: RPCClient } = require('@xhayper/discord-rpc');

const DOWNLOAD_CONCURRENCY = 4;

const CONFIG_FILE = path.join(app.getPath('userData'), 'config.json');
const APP_NAME = 'AMURKA PVE MOD';
const SERVER_IP = '212.22.93.89:20022';
const GITHUB_REPO = 'AMURKA-PVE-SCUM/amurka-pve-mods';
const GITHUB_TOKEN = process.env.GITHUB_TOKEN || '';
const WARGM_VOTE_URL = 'https://wargm.ru/server/77385/votes';
const WARGM_SHOP_URL = 'https://wargm.ru/server/77385/shop';
const DISCORD_URL = 'https://discord.gg/CApw7CYBtA';
const LOLKA_URL = 'https://lolka.gg/nmgHA2I';
const RPC_CLIENT_ID = '760603147338753';

let win;
let rpc;

function getConfig() {
  try {
    if (fs.existsSync(CONFIG_FILE)) {
      return JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf-8'));
    }
  } catch {}
  return {};
}

function saveConfig(data) {
  try {
    const dir = path.dirname(CONFIG_FILE);
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
    const existing = getConfig();
    Object.assign(existing, data);
    fs.writeFileSync(CONFIG_FILE, JSON.stringify(existing, null, 2));
    return true;
  } catch { return false; }
}

function scumExePath(gamePath) {
  for (const p of [
    path.join(gamePath, 'SCUM', 'Binaries', 'Win64', 'SCUM.exe'),
    path.join(gamePath, 'Binaries', 'Win64', 'SCUM.exe'),
  ]) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

function getRegValue(key, name) {
  try {
    const buf = require('child_process').execSync(
      `reg query "${key}" /v "${name}"`, { encoding: 'utf-8', timeout: 5000, stdio: ['pipe', 'pipe', 'pipe'] }
    );
    const re = new RegExp(`\\s${name}\\s+REG_\\w+\\s+(.+)`);
    const m = buf.match(re);
    return m ? m[1].trim().replace(/\\/g, '/') : null;
  } catch { return null; }
}

function autoDetectScum() {
  const candidates = new Set();

  // 1) Steam из реестра (самый надёжный способ)
  const regPaths = [
    getRegValue('HKEY_CURRENT_USER\\Software\\Valve\\Steam', 'SteamPath'),
    getRegValue('HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\Valve\\Steam', 'InstallPath'),
    getRegValue('HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam', 'InstallPath'),
  ];
  for (const rp of regPaths) {
    if (rp && fs.existsSync(rp)) candidates.add(path.normalize(rp));
  }

  // 2) Стандартные пути
  for (const base of [
    'C:\\Program Files (x86)\\Steam',
    'C:\\Program Files\\Steam',
  ]) {
    if (fs.existsSync(base)) candidates.add(base);
  }

  // 3) Поиск по дискам
  for (const drive of 'DEFGHIJKLMNOPQRSTUVWXYZ') {
    for (const folder of ['Steam', 'SteamLibrary']) {
      const p = `${drive}:\\${folder}`;
      if (fs.existsSync(p)) candidates.add(p);
    }
  }

  // 4) Все библиотеки из libraryfolders.vdf
  for (const steamBase of [...candidates]) {
    const vdf = path.join(steamBase, 'steamapps', 'libraryfolders.vdf');
    if (fs.existsSync(vdf)) {
      try {
        const content = fs.readFileSync(vdf, 'utf-8');
        const matches = content.match(/"path"\s+"([^"]+)"/g);
        if (matches) {
          for (const m of matches) {
            const libPath = m.split('"')[3].replace(/\\\\/g, '\\');
            if (fs.existsSync(libPath)) candidates.add(path.normalize(libPath));
          }
        }
      } catch {}
    }
  }

  // 5) Быстрый поиск по appmanifest (SCUM AppID = 513710)
  for (const lib of candidates) {
    const manifest = path.join(lib, 'steamapps', 'appmanifest_513710.acf');
    if (fs.existsSync(manifest)) {
      try {
        const content = fs.readFileSync(manifest, 'utf-8');
        const m = content.match(/"installdir"\s+"([^"]+)"/);
        if (m) {
          const scum = path.join(lib, 'steamapps', 'common', m[1]);
          if (scumExePath(scum)) return scum;
        }
      } catch {}
    }
  }

  // 6) Полный перебор common/SCUM во всех библиотеках
  for (const lib of candidates) {
    const scum = path.join(lib, 'steamapps', 'common', 'SCUM');
    if (scumExePath(scum)) return scum;
  }

  return null;
}

function modsPathFor(gamePath) {
  const p1 = path.join(gamePath, 'SCUM', 'Content', 'Paks', '~mods');
  const p2 = path.join(gamePath, 'Content', 'Paks', '~mods');
  if (fs.existsSync(p1)) return p1;
  if (fs.existsSync(p2)) return p2;
  if (scumExePath(path.join(gamePath, 'SCUM'))) return p1;
  return p2;
}

function httpsGet(url, token) {
  return new Promise((resolve, reject) => {
    const proto = url.startsWith('https') ? https : http;
    const headers = { 'User-Agent': 'AMURKA-Launcher/2.0' };
    if (token) headers['Authorization'] = `token ${token}`;
    const req = proto.get(url, { headers }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        httpsGet(res.headers.location, token).then(resolve).catch(reject);
        return;
      }
      let data = '';
      res.on('data', (chunk) => data += chunk);
      res.on('end', () => {
        if (res.statusCode >= 400) reject(new Error(`HTTP ${res.statusCode}`));
        else resolve(data);
      });
    });
    req.on('error', reject);
    req.setTimeout(30000, () => { req.destroy(); reject(new Error('Timeout')); });
  });
}

function httpsDownload(url, destPath, onProgress) {
  return new Promise((resolve, reject) => {
    const proto = url.startsWith('https') ? https : http;

    let startOffset = 0;
    try {
      if (fs.existsSync(destPath)) startOffset = fs.statSync(destPath).size;
    } catch {}

    const headers = { 'User-Agent': 'AMURKA-Launcher/2.0' };
    if (startOffset > 0) headers['Range'] = `bytes=${startOffset}-`;

    const req = proto.get(url, { headers }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        httpsDownload(res.headers.location, destPath, onProgress).then(resolve).catch(reject);
        return;
      }
      if (res.statusCode === 416) {
        if (startOffset > 0) startOffset = 0;
        httpsDownload(url, destPath, onProgress).then(resolve).catch(reject);
        return;
      }
      if (res.statusCode >= 400) {
        reject(new Error(`HTTP ${res.statusCode}`));
        return;
      }

      const contentLength = parseInt(res.headers['content-length'] || '0', 10);
      const total = startOffset + contentLength;
      let downloaded = startOffset;

      const file = fs.createWriteStream(destPath, { flags: startOffset > 0 ? 'r+' : 'w', start: startOffset });

      res.on('data', (chunk) => {
        downloaded += chunk.length;
        file.write(chunk);
        if (onProgress && total) onProgress(downloaded, total);
      });
      res.on('end', () => { file.end(); resolve(); });
      res.on('error', (e) => { file.close(); if (!startOffset) try { fs.unlinkSync(destPath); } catch {} reject(e); });
    });
    req.on('error', reject);
    req.setTimeout(60000, () => { req.destroy(); reject(new Error('Timeout')); });
  });
}

// --- IPC Handlers ---

ipcMain.handle('get-config', () => getConfig());
ipcMain.handle('save-config', (_, data) => saveConfig(data));
ipcMain.handle('get-constants', () => ({
  serverIp: SERVER_IP,
  appName: APP_NAME,
  wargmVote: WARGM_VOTE_URL,
  wargmShop: WARGM_SHOP_URL,
  discord: DISCORD_URL,
  lolka: LOLKA_URL,
}));

ipcMain.handle('detect-scum', () => autoDetectScum());
ipcMain.handle('find-game-exe', (_, gamePath) => scumExePath(gamePath));
ipcMain.handle('get-mods-path', (_, gamePath) => modsPathFor(gamePath));

ipcMain.handle('fetch-mods', async () => {
  const contentsUrl = `https://api.github.com/repos/${GITHUB_REPO}/contents`;
  const releaseUrl = `https://api.github.com/repos/${GITHUB_REPO}/releases/latest`;

  const [contentsRaw, releaseRaw] = await Promise.all([
    httpsGet(contentsUrl, GITHUB_TOKEN).catch(() => '[]'),
    httpsGet(releaseUrl, GITHUB_TOKEN).catch(() => null),
  ]);

  const modMap = new Map();

  const contentsItems = JSON.parse(contentsRaw);
  for (const item of contentsItems) {
    if (item.type === 'file' && item.name.toLowerCase().endsWith('.pak')) {
      modMap.set(item.name, {
        name: item.name,
        downloadUrl: `https://raw.githubusercontent.com/${GITHUB_REPO}/main/${item.name}`,
        size: item.size,
      });
    }
  }

  if (releaseRaw) {
    try {
      const release = JSON.parse(releaseRaw);
      if (release.assets) {
        for (const asset of release.assets) {
          if (asset.name === 'ImprovedMap.pak') continue;
          if (asset.name.toLowerCase().endsWith('.pak')) {
            modMap.set(asset.name, {
              name: asset.name,
              downloadUrl: asset.browser_download_url,
              size: asset.size,
            });
          }
        }
      }
    } catch {}
  }

  return [...modMap.values()];
});

ipcMain.handle('scan-mods', (_, modsPath) => {
  if (!fs.existsSync(modsPath)) return [];
  return fs.readdirSync(modsPath).filter((f) => f.toLowerCase().endsWith('.pak'));
});

ipcMain.handle('download-all-mods', async (event, mods, modsPath) => {
  if (!fs.existsSync(modsPath)) fs.mkdirSync(modsPath, { recursive: true });

  const queue = mods.filter((m) => !fs.existsSync(path.join(modsPath, m.name)));
  const totalBytes = queue.reduce((s, m) => s + (m.size || 0), 0);
  let downloadedBytes = 0, success = mods.length - queue.length, errors = 0;
  let lastSentPct = -1;
  const fileBytes = new Map();

  function sendProgress(file) {
    const bytes = [...fileBytes.values()].reduce((s, v) => s + v, 0);
    const pct = totalBytes > 0 ? Math.min(100, Math.round((bytes / totalBytes) * 100)) : 0;
    if (pct !== lastSentPct) {
      lastSentPct = pct;
      event.sender.send('download-progress', { percent: pct, file, downloaded: bytes, total: totalBytes });
    }
  }

  async function worker() {
    while (queue.length) {
      const mod = queue.shift();
      const dest = path.join(modsPath, mod.name);
      if (fs.existsSync(dest)) { success++; continue; }

      try {
        fileBytes.set(mod.name, 0);
        await httpsDownload(mod.downloadUrl, dest, (downloaded, total) => {
          fileBytes.set(mod.name, downloaded);
          sendProgress(mod.name);
        });
        fileBytes.set(mod.name, mod.size || 0);
        sendProgress(mod.name);
        success++;
      } catch (e) {
        errors++;
      }
    }
  }

  await Promise.all([...Array(DOWNLOAD_CONCURRENCY)].map(() => worker()));
  event.sender.send('download-progress', { percent: 100, file: 'Завершение...' });
  return { success, errors };
});

ipcMain.handle('delete-all-mods', (_, modsPath) => {
  if (!fs.existsSync(modsPath)) return 0;
  let removed = 0;
  for (const f of fs.readdirSync(modsPath)) {
    if (f.toLowerCase().endsWith('.pak')) {
      try { fs.unlinkSync(path.join(modsPath, f)); removed++; } catch {}
    }
  }
  return removed;
});

ipcMain.handle('launch-game', (_, { gamePath, mode }) => {
  const exe = scumExePath(gamePath);
  if (!exe) throw new Error('SCUM.exe not found');

  const args = ['-nobattleye', '-skipfilestreamingchecks', '-nohmd', '-nosplash', '-fileopenlog'];
  if (mode === 'dx12') args.push('-dx12');
  else if (mode === 'vulkan') args.push('-vulkan');

  spawn(exe, args, { detached: true, stdio: 'ignore' }).unref();
  return true;
});

ipcMain.handle('open-url', (_, url) => shell.openExternal(url));
ipcMain.handle('copy-text', (_, text) => {
  clipboard.writeText(text);
  return true;
});
ipcMain.handle('browse-folder', async (_, defaultPath) => {
  const result = await dialog.showOpenDialog(win, {
    properties: ['openDirectory'],
    defaultPath: defaultPath || 'C:\\',
  });
  if (result.canceled) return null;
  return result.filePaths[0];
});

function initRPC() {
  try {
    const client = new RPCClient({ clientId: RPC_CLIENT_ID });

    client.on('ready', async () => {
      try {
        await client.user?.setActivity({
          details: 'AMURKA PVE',
          state: '212.22.93.89:20022',
          startTimestamp: Date.now(),
          largeImageKey: 'amurka',
          largeImageText: 'AMURKA PVE',
          instance: false,
        });
      } catch {}
    });

    client.on('disconnected', () => {
      setTimeout(() => {
        if (!rpc?.isConnected) initRPC();
      }, 10000);
    });

    client.connect().catch(() => {});
    rpc = client;
  } catch {}
}

function createWindow() {
  win = new BrowserWindow({
    width: 960,
    height: 620,
    minWidth: 800,
    minHeight: 520,
    frame: false,
    transparent: false,
    backgroundColor: '#0f0c29',
    icon: path.join(__dirname, 'assets', 'icon.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });

  win.loadFile(path.join(__dirname, 'src', 'index.html'));
  win.setMenu(null);
}

ipcMain.on('window-minimize', () => win?.minimize());
ipcMain.on('window-maximize', () => { if (win?.isMaximized()) win.unmaximize(); else win?.maximize(); });
ipcMain.on('window-close', () => win?.close());

app.whenReady().then(() => {
  createWindow();
  initRPC();
});
app.on('window-all-closed', () => app.quit());
app.on('will-quit', () => {
  if (rpc) { rpc.destroy().catch(() => {}); }
});
