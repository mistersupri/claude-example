const { app, BrowserWindow, Menu, dialog, ipcMain, shell } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');
const scanners = require('./src/scanners');
const jobs = require('./src/jobs');
const { jpegsToPdf } = require('./src/pdf');

const FORMATS = new Set(['pdf', 'jpg']);
const SOURCES = new Set(['flatbed', 'adf']);
const COLORS = new Set(['color', 'gray']);
const DPIS = new Set([150, 200, 300, 600]);

async function uniquePath(file) {
  const { dir, name, ext } = path.parse(file);
  let candidate = file;
  for (let n = 2; ; n++) {
    try {
      await fs.access(candidate);
      candidate = path.join(dir, `${name} (${n})${ext}`);
    } catch {
      return candidate;
    }
  }
}

async function saveScan({ baseDir, folder, name, pages, format, dpi }) {
  const dir = path.join(baseDir, ...jobs.folderParts(folder.join('/')));
  const rel = path.relative(baseDir, dir);
  if (rel.startsWith('..') || path.isAbsolute(rel)) throw new Error('Folder tujuan tidak valid');
  await fs.mkdir(dir, { recursive: true });
  const safeName = jobs.cleanName(name);
  if (!safeName) throw new Error('Nama berkas kosong');

  if (format === 'pdf') {
    const file = await uniquePath(path.join(dir, `${safeName}.pdf`));
    await fs.writeFile(file, jpegsToPdf(pages, dpi));
    return [file];
  }
  const files = [];
  for (let i = 0; i < pages.length; i++) {
    const file = await uniquePath(path.join(dir, i === 0 ? `${safeName}.jpg` : `${safeName}-${i + 1}.jpg`));
    await fs.writeFile(file, pages[i]);
    files.push(file);
  }
  return files;
}

function registerIpc() {
  ipcMain.handle('app:defaults', () => ({ outDir: path.join(app.getPath('documents'), 'DocScan') }));
  ipcMain.handle('scanners:discover', () => scanners.discoverAll());
  ipcMain.handle('scanners:addManual', (_e, address) => scanners.addManual(address));

  ipcMain.handle('dialog:folder', async (e) => {
    const win = BrowserWindow.fromWebContents(e.sender);
    const res = await dialog.showOpenDialog(win, { properties: ['openDirectory', 'createDirectory'] });
    return res.canceled ? null : res.filePaths[0];
  });
  ipcMain.handle('dialog:table', async (e) => {
    const win = BrowserWindow.fromWebContents(e.sender);
    const res = await dialog.showOpenDialog(win, {
      properties: ['openFile'],
      filters: [{ name: 'Tabel teks', extensions: ['csv', 'tsv', 'txt'] }],
    });
    if (res.canceled) return null;
    const stat = await fs.stat(res.filePaths[0]);
    if (stat.size > 5 * 1024 * 1024) throw new Error('File lebih dari 5 MB');
    return { name: path.basename(res.filePaths[0]), text: await fs.readFile(res.filePaths[0], 'utf8') };
  });

  ipcMain.handle('table:parse', (_e, text) => jobs.parseDelimited(text));
  ipcMain.handle('jobs:build', (_e, req) =>
    req.mode === 'table'
      ? jobs.buildFromTable(req.rows, { folderCol: req.folderCol, nameCol: req.nameCol })
      : jobs.buildFromList(req.text),
  );

  ipcMain.handle('scan:run', async (_e, req) => {
    if (!FORMATS.has(req.format) || !SOURCES.has(req.source) || !COLORS.has(req.color) || !DPIS.has(req.dpi)) {
      throw new Error('Pengaturan pindai tidak valid');
    }
    if (!Array.isArray(req.folderParts) || typeof req.name !== 'string') throw new Error('Data berkas tidak valid');
    if (typeof req.outDir !== 'string' || !path.isAbsolute(req.outDir)) throw new Error('Folder tujuan belum dipilih');
    const pages = await scanners.scan(req.scannerId, {
      source: req.source === 'adf' ? 'adf' : 'flatbed',
      dpi: req.dpi,
      color: req.color,
    });
    const files = await saveScan({
      baseDir: req.outDir,
      folder: req.folderParts,
      name: req.name,
      pages,
      format: req.format,
      dpi: req.dpi,
    });
    return { files, pageCount: pages.length };
  });

  ipcMain.handle('shell:openFolder', async (_e, dir) => {
    if (typeof dir !== 'string' || !path.isAbsolute(dir)) return;
    await fs.mkdir(dir, { recursive: true });
    await shell.openPath(dir);
  });
}

function createWindow() {
  const win = new BrowserWindow({
    width: 1100,
    height: 760,
    minWidth: 720,
    minHeight: 560,
    title: 'DocScan',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
    },
  });
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

app.whenReady().then(() => {
  if (process.platform !== 'darwin') Menu.setApplicationMenu(null);
  registerIpc();
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
