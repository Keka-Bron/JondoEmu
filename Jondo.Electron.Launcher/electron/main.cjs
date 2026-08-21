const { app, BrowserWindow, dialog, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const AdmZip = require('adm-zip');

const VERSION = '3.6.10.10';
const MELON_URL = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip';
const MELON_SHA256 = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416';
const MAX_ZIP_BYTES = 100 * 1024 * 1024;
const activeClients = new Map();
const GAME_LANGUAGES = new Set(['en', 'fr', 'es', 'de', 'pt']);

function preferencePath() { return path.join(app.getPath('userData'), 'launcher.json'); }
function defaults() { return { endpoint: 'http://127.0.0.1:8888', clientPath: '', language: 'en', accounts: [], snapshotPath: '' }; }
async function preferences() {
  try { return { ...defaults(), ...JSON.parse(await fs.readFile(preferencePath(), 'utf8')) }; }
  catch { return defaults(); }
}
async function savePreferences(value) {
  const safe = { ...defaults(), ...value };
  safe.accounts = Array.isArray(safe.accounts) ? safe.accounts.slice(0, 8) : [];
  await fs.mkdir(path.dirname(preferencePath()), { recursive: true });
  await fs.writeFile(preferencePath(), JSON.stringify(safe, null, 2), 'utf8');
  return safe;
}
function endpoint(value) {
  const raw = String(value || '').trim() || defaults().endpoint;
  const normalized = raw.includes('://') ? raw : `http://${raw}`;
  const parsed = new URL(normalized);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password || parsed.pathname !== '/') throw new Error('Enter a host or an HTTP(S) control URL without a path.');
  return parsed;
}
function serverHost(value) { return endpoint(value).hostname; }
function gameLanguage(value) {
  const language = String(value || 'en').trim().toLowerCase();
  if (!GAME_LANGUAGES.has(language)) throw new Error('Choose a supported Dofus language.');
  return language;
}
async function api(endpointValue, route, body = {}) {
  const base = endpoint(endpointValue);
  const url = new URL(`/api/${route}`, base);
  const response = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  const text = await response.text();
  let data = {};
  try { data = text ? JSON.parse(text) : {}; } catch { throw new Error('The server returned invalid JSON.'); }
  if (!response.ok) throw new Error(data.error === 'https-required' ? 'The public server requires its HTTPS control URL.' : data.error || `Server returned ${response.status}.`);
  return data;
}
async function validateDofus(file) {
  if (!file || path.basename(file).toLowerCase() !== 'dofus.exe') throw new Error('Choose the Dofus.exe executable.');
  const stat = await fs.stat(file);
  if (!stat.isFile()) throw new Error('Dofus.exe was not found.');
  const gameDir = path.dirname(file);
  await fs.stat(path.join(gameDir, 'Dofus_Data'));
  return gameDir;
}
async function supportStatus(file) {
  try {
    const gameDir = await validateDofus(file);
    await fs.stat(path.join(gameDir, 'MelonLoader', 'net6', 'MelonLoader.dll'));
    await fs.stat(path.join(gameDir, 'Mods', 'JondoFix.dll'));
    return { ready: true, message: 'MelonLoader and JondoFix are ready.' };
  } catch (error) { return { ready: false, message: error.message || 'Client support is not ready.' }; }
}
function sha256(data) { return crypto.createHash('sha256').update(data).digest('hex').toUpperCase(); }
function bundledFix() {
  return app.isPackaged ? path.join(process.resourcesPath, 'JondoFix.dll') : path.resolve(__dirname, '../../JondoFix/JondoFix.dll');
}
async function installSupport(file) {
  const gameDir = await validateDofus(file);
  if ([...activeClients.values()].some(client => client.gameDir === gameDir && !client.child.killed)) throw new Error('Close Dofus before changing client support.');
  const response = await fetch(MELON_URL, { redirect: 'follow' });
  if (!response.ok) throw new Error(`Could not download MelonLoader (${response.status}).`);
  const archive = Buffer.from(await response.arrayBuffer());
  if (archive.length > MAX_ZIP_BYTES || sha256(archive) !== MELON_SHA256) throw new Error('The MelonLoader package failed its integrity check.');
  const zip = new AdmZip(archive);
  for (const entry of zip.getEntries()) {
    if (entry.isDirectory) continue;
    const name = entry.entryName.replace(/\\/g, '/');
    const allowed = name.startsWith('MelonLoader/') || name === 'version.dll' || name === 'dobby.dll';
    if (!allowed || name.includes('..')) throw new Error(`Unexpected path in MelonLoader package: ${name}`);
    const target = path.resolve(gameDir, name);
    if (!target.startsWith(path.resolve(gameDir) + path.sep)) throw new Error('Unsafe archive path.');
    await fs.mkdir(path.dirname(target), { recursive: true });
    await fs.writeFile(target, entry.getData());
  }
  const fix = bundledFix();
  await fs.stat(fix);
  await fs.mkdir(path.join(gameDir, 'Mods'), { recursive: true });
  await fs.copyFile(fix, path.join(gameDir, 'Mods', 'JondoFix.dll'));
  return supportStatus(file);
}
async function catalogManifest(folder) {
  const root = path.resolve(folder || '');
  const manifestPath = path.join(root, 'manifest.json');
  const manifest = JSON.parse(await fs.readFile(manifestPath, 'utf8'));
  if (manifest.clientVersion !== VERSION || !Array.isArray(manifest.catalogs)) throw new Error(`Choose a ${VERSION} client_data snapshot folder.`);
  return { root, manifest };
}
function cataloguePath(root, output) {
  const target = path.resolve(root, output);
  if (!target.startsWith(root + path.sep) || path.extname(target) !== '.json') throw new Error('Invalid catalogue path.');
  return target;
}

function createWindow() {
  const window = new BrowserWindow({
    width: 1320, height: 820, minWidth: 920, minHeight: 620, show: false,
    frame: false, titleBarStyle: 'hidden', title: 'Jondo Launcher', backgroundColor: '#120b07',
    icon: path.resolve(__dirname, '../../launcher_assets/icon.ico'),
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false }
  });
  window.on('maximize', () => window.webContents.send('window:maximized', true));
  window.on('unmaximize', () => window.webContents.send('window:maximized', false));
  window.once('ready-to-show', () => window.show());
  const devUrl = process.env.VITE_DEV_SERVER_URL;
  if (devUrl) window.loadURL(devUrl); else window.loadFile(path.join(__dirname, '../build/renderer/index.html'));
}
app.whenReady().then(() => { createWindow(); app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); }); });
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });

ipcMain.handle('preferences:get', preferences);
ipcMain.handle('preferences:save', (_, value) => savePreferences(value));
ipcMain.handle('window:minimize', event => BrowserWindow.fromWebContents(event.sender)?.minimize());
ipcMain.handle('window:toggle-maximize', event => {
  const window = BrowserWindow.fromWebContents(event.sender);
  if (!window) return false;
  if (window.isMaximized()) window.unmaximize(); else window.maximize();
  return window.isMaximized();
});
ipcMain.handle('window:close', event => BrowserWindow.fromWebContents(event.sender)?.close());
ipcMain.handle('server:status', async () => api((await preferences()).endpoint, 'estado'));
ipcMain.handle('account:sign-in', async (_, input) => {
  const endpointValue = input.endpoint;
  const result = await api(endpointValue, 'entrar', { usuario: input.login, clave: input.password });
  if (!result.bien) return result;
  return { ...result, endpoint: endpoint(endpointValue).toString().replace(/\/$/, '') };
});
ipcMain.handle('account:register', async (_, input) => api(input.endpoint, 'crear-cuenta', { usuario: input.login, clave: input.password, apodo: input.nickname }));
ipcMain.handle('account:remember', async (_, input) => api(input.endpoint, 'recordar-token', { cuenta: input.accountId, token: input.token }));
ipcMain.handle('client:choose', async () => { const result = await dialog.showOpenDialog({ title: 'Choose Dofus.exe', properties: ['openFile'], filters: [{ name: 'Dofus executable', extensions: ['exe'] }] }); return result.canceled ? '' : result.filePaths[0]; });
ipcMain.handle('client:support-status', (_, file) => supportStatus(file));
ipcMain.handle('client:install-support', (_, file) => installSupport(file));
ipcMain.handle('client:launch', async (_, input) => {
  const gameDir = await validateDofus(input.clientPath);
  const status = await supportStatus(input.clientPath);
  if (!status.ready) throw new Error('Install verified client support before launching.');
  const language = gameLanguage(input.language);
  const launch = await api(input.endpoint, 'lanzamiento', { token: input.token, idioma: language });
  if (!launch.bien) throw new Error(launch.motivo || 'The server rejected this launch.');
  // This exactly mirrors the language hand-off in the installed client's zaap.yml.  It is an
  // argument, not a write into the official installation, so multiple launcher profiles remain safe.
  const args = ['-force-d3d11', '-screen-fullscreen', '0', '--melonloader.hideconsole', '--melonloader.disablestartscreen', '--port', '15881', '--gameName', 'dofus', '--gameRelease', 'dofus3', '--instanceId', String(launch.instancia), '--hash', launch.hash, '--canLogin', 'true', '--langCode', language, '--autoConnectType', '1', '--connectionPort', '5555'];
  const child = spawn(input.clientPath, args, { cwd: gameDir, detached: false, env: { ...process.env, ZAAP_PORT: '15881', ZAAP_HASH: launch.hash, ZAAP_GAME: 'dofus', ZAAP_RELEASE: 'dofus3', ZAAP_INSTANCE_ID: String(launch.instancia), ZAAP_CAN_AUTH: 'true', JONDO_SERVER_HOST: serverHost(input.endpoint), JONDO_GAME_LANGUAGE: language } });
  activeClients.set(child.pid, { child, gameDir });
  child.once('exit', () => { activeClients.delete(child.pid); api(input.endpoint, 'fin-de-lanzamiento', { token: input.token }).catch(() => {}); });
  return { pid: child.pid };
});
ipcMain.handle('catalog:choose-snapshot', async () => { const result = await dialog.showOpenDialog({ title: 'Choose client_data version folder', properties: ['openDirectory'] }); return result.canceled ? '' : result.filePaths[0]; });
ipcMain.handle('catalog:open', async (_, folder) => { const { root, manifest } = await catalogManifest(folder); return { root, manifest }; });
ipcMain.handle('catalog:rows', async (_, input) => { const { root } = await catalogManifest(input.root); const file = cataloguePath(root, input.output); const document = JSON.parse(await fs.readFile(file, 'utf8')); const rows = Array.isArray(document.rows) ? document.rows : (document.items || document.sets || document.mounts || []); const query = String(input.query || '').toLowerCase(); const filtered = query ? rows.filter(row => JSON.stringify(row).toLowerCase().includes(query)) : rows; const offset = Math.max(0, Number(input.offset) || 0); return { total: filtered.length, rows: filtered.slice(offset, offset + 100) }; });
