const { app, BrowserWindow, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const fsSync = require('node:fs');
const path = require('node:path');

function root() {
  if (!app.isPackaged) return path.resolve(__dirname, '../..');

  // electron-builder portable apps normally provide PORTABLE_EXECUTABLE_DIR, but
  // that variable is absent for some shell launches.  In that case process.execPath
  // can point at the temporary extraction folder, not the emulator workspace.  Find
  // the directory that actually owns tools/ before accepting a fallback so tool
  // selection never crashes just because the portable wrapper used a different path.
  const portableDirectory = process.env.PORTABLE_EXECUTABLE_DIR;
  const candidates = [portableDirectory, process.env.PORTABLE_EXECUTABLE_FILE && path.dirname(process.env.PORTABLE_EXECUTABLE_FILE), process.cwd(), path.dirname(process.execPath)]
    .filter(Boolean)
    .map(candidate => path.resolve(candidate));
  // A portable Electron executable temporarily extracts itself before launching.  If
  // the wrapper did not expose PORTABLE_EXECUTABLE_DIR, walk only the immediate
  // ancestors of known paths; this still finds a checkout launched from a child
  // folder without accidentally scanning the drive or blocking the renderer.
  const workspace = candidates.map(candidate => {
    let current = candidate;
    for (let depth = 0; depth < 6; depth += 1) {
      if (fsSync.existsSync(path.join(current, 'tools'))) return current;
      const parent = path.dirname(current);
      if (parent === current) break;
      current = parent;
    }
    return null;
  }).find(Boolean);
  return workspace || path.dirname(process.execPath);
}
function toolRoot() { return path.join(root(), 'tools'); }
function safeName(name) { return typeof name === 'string' && /^[a-zA-Z0-9_-]+\.py$/.test(name) ? name : null; }
async function listTools() {
  // Tool discovery is a UI convenience.  A bad portable launch directory must
  // produce an empty catalogue, not terminate the Electron main process.
  let files = [];
  try { files = await fs.readdir(toolRoot(), { withFileTypes: true }); } catch { return []; }
  const available = new Set(files.filter(file => file.isFile() && safeName(file.name)).map(file => file.name));
  let index = [];
  try { index = JSON.parse(await fs.readFile(path.join(toolRoot(), 'tool_index.json'), 'utf8')).tools || []; } catch { /* graceful fallback for source checkouts */ }
  return index.filter(tool => available.has(tool.script)).map(tool => ({ ...tool, name: tool.script, path: path.join(toolRoot(), tool.script) }));
}
function createWindow() {
  const window = new BrowserWindow({ width: 1280, height: 820, minWidth: 900, minHeight: 620, frame: false, titleBarStyle: 'hidden', title: 'Jondo Admin', backgroundColor: '#f4f7fb', icon: path.join(root(), 'launcher_assets', 'icon.ico'), webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false } });
  const dev = process.env.VITE_DEV_SERVER_URL; if (dev) window.loadURL(dev); else window.loadFile(path.join(__dirname, '../build/renderer/index.html'));
}
app.whenReady().then(createWindow); app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
ipcMain.handle('window:minimize', event => BrowserWindow.fromWebContents(event.sender)?.minimize());
ipcMain.handle('window:toggle', event => { const w = BrowserWindow.fromWebContents(event.sender); if (w?.isMaximized()) w.unmaximize(); else w?.maximize(); });
ipcMain.handle('window:close', event => BrowserWindow.fromWebContents(event.sender)?.close());
ipcMain.handle('tools:list', listTools);
ipcMain.handle('tools:run', async (_, input) => {
  const name = safeName(input?.name); if (!name) throw new Error('Invalid tool selection.');
  const tools = await listTools(); if (!tools.some(tool => tool.name === name)) throw new Error('Tool is not in the emulator tools directory.');
  const args = Array.isArray(input?.args) && input.args.every(value => typeof value === 'string' && value.length <= 512) ? input.args : [];
  return await new Promise((resolve) => {
    const child = spawn('py', ['-3', path.join(toolRoot(), name), ...args], { cwd: root(), windowsHide: true }); let output = '';
    child.stdout.on('data', data => { output = (output + data).slice(-200000); }); child.stderr.on('data', data => { output = (output + data).slice(-200000); });
    child.on('error', error => resolve({ code: -1, output: output + error.message })); child.on('close', code => resolve({ code, output }));
  });
});
ipcMain.handle('telemetry:summary', async () => {
  return await new Promise(resolve => { const child = spawn('py', ['-3', path.join(toolRoot(), 'review_unknown_packets.py'), '--summary'], { cwd: root(), windowsHide: true }); let output = ''; child.stdout.on('data', d => output += d); child.stderr.on('data', d => output += d); child.on('close', () => { try { resolve(JSON.parse(output)); } catch { resolve({ error: output }); } }); });
});
function telemetryCommand(args) {
  return new Promise(resolve => { const child = spawn('py', ['-3', path.join(toolRoot(), 'review_unknown_packets.py'), ...args], { cwd: root(), windowsHide: true }); let output = ''; child.stdout.on('data', d => output += d); child.stderr.on('data', d => output += d); child.on('close', () => { try { resolve(JSON.parse(output)); } catch { resolve({ error: output }); } }); });
}
ipcMain.handle('telemetry:list', () => telemetryCommand(['--all']));
ipcMain.handle('telemetry:detail', (_, id) => Number.isInteger(id) && id > 0 ? telemetryCommand(['--id', String(id)]) : ({ error: 'Invalid packet id.' }));
function databaseBridge(request) {
  return new Promise((resolve, reject) => {
    const child = spawn('py', ['-3', path.join(toolRoot(), 'admin_database.py'), JSON.stringify(request)], { cwd: root(), windowsHide: true });
    let output = ''; child.stdout.on('data', data => output = (output + data).slice(-1000000)); child.stderr.on('data', data => output = (output + data).slice(-1000000));
    child.on('error', reject); child.on('close', code => { try { const result = JSON.parse(output); if (code !== 0 || result.error) reject(new Error(result.error || output)); else resolve(result); } catch { reject(new Error(output || `Database bridge exited with ${code}.`)); } });
  });
}
ipcMain.handle('database:list', () => databaseBridge({ action: 'databases' }));
ipcMain.handle('database:tables', (_, database) => databaseBridge({ action: 'tables', database }));
ipcMain.handle('database:rows', (_, input) => databaseBridge({ action: 'rows', database: input?.database, table: input?.table, page: input?.page, pageSize: input?.pageSize }));
