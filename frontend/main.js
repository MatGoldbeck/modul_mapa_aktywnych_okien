const { app, BrowserWindow, Tray, Menu, ipcMain, screen, dialog, nativeImage } = require('electron');
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

// Okno siatki zajmuje taką część obszaru roboczego monitora głównego (stały rozmiar).
const WINDOW_SCALE = 0.85;

let gridWindow = null;
let tray = null;
let backend = null;
let quitting = false;

// Jedna instancja – ponowne uruchomienie tylko pokazuje istniejące okno.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => showGrid());
  app.whenReady().then(start);
}

function start() {
  startBackend();
  createGridWindow();
  createTray();
  tray.displayBalloon({
    title: 'Mapa aktywnych okien działa w tle',
    content: 'Przytrzymaj Ctrl+Shift i przesuń mysz, aby otworzyć mapę. Wyłączenie: ikona w zasobniku → Zakończ.',
    iconType: 'info',
  });
}

// --- Backend (.NET) --------------------------------------------------------

function backendPath() {
  if (process.env.MAPA_BACKEND) return process.env.MAPA_BACKEND;
  const base = path.join(__dirname, '..', 'backend', 'bin');
  return ['Release', 'Debug']
    .map((cfg) => path.join(base, cfg, 'net9.0-windows', 'MapaOkien.Backend.exe'))
    .find((p) => fs.existsSync(p));
}

function startBackend() {
  const exe = backendPath();
  if (!exe) {
    dialog.showErrorBox(
      'Mapa aktywnych okien – brak backendu',
      'Nie znaleziono zbudowanego backendu (backend\\bin\\Release\\net9.0-windows\\MapaOkien.Backend.exe).\n\n' +
        'Uruchom program przez start.cmd w głównym folderze repozytorium – zbuduje backend automatycznie.'
    );
    return;
  }
  // Backend kończy się sam, gdy zniknie ten proces (--parent-pid), także po awarii frontendu.
  backend = spawn(exe, ['--parent-pid', String(process.pid)], { windowsHide: true, stdio: 'ignore' });
  backend.on('exit', (code) => {
    backend = null;
    if (!quitting && code !== 0) {
      tray?.displayBalloon({
        title: 'Backend zakończył działanie',
        content: `Kod ${code}. Szczegóły: %APPDATA%\\MapaAktywnychOkien\\backend.log`,
        iconType: 'error',
      });
    }
  });
}

// --- Okno siatki -----------------------------------------------------------

function createGridWindow() {
  const { workArea } = screen.getPrimaryDisplay();
  const width = Math.round(workArea.width * WINDOW_SCALE);
  const height = Math.round(workArea.height * WINDOW_SCALE);

  gridWindow = new BrowserWindow({
    width,
    height,
    x: workArea.x + Math.round((workArea.width - width) / 2),
    y: workArea.y + Math.round((workArea.height - height) / 2),
    show: false, // ukryte do momentu gestu
    resizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    autoHideMenuBar: true,
    title: 'Mapa aktywnych okien',
    icon: path.join(__dirname, 'assets', 'icon.png'),
    backgroundColor: '#17181c',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      // Okno jest większość czasu ukryte – WebSocket i timery muszą działać bez spowolnienia.
      backgroundThrottling: false,
    },
  });
  gridWindow.setMenu(null);
  gridWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));

  // Zamknięcie krzyżykiem tylko chowa okno – program działa dalej w tle (wyłączenie: zasobnik → Zakończ).
  gridWindow.on('close', (e) => {
    if (!quitting) {
      e.preventDefault();
      gridWindow.hide();
    }
  });

  let moveTimer = null;
  gridWindow.on('moved', () => {
    clearTimeout(moveTimer);
    moveTimer = setTimeout(() => {
      const [x, y] = gridWindow.getPosition();
      gridWindow.webContents.send('grid:moved', { x, y });
    }, 300);
  });
}

function showGrid() {
  if (!gridWindow) return;
  gridWindow.show();
  gridWindow.focus();
  gridWindow.webContents.send('grid:shown');
}

/** Przywraca zapamiętaną pozycję, o ile okno zmieści się na którymś z monitorów. */
function applySavedPosition(pos) {
  if (!gridWindow || !pos || gridWindow.isVisible()) return;
  const [width, height] = gridWindow.getSize();
  const fits = screen.getAllDisplays().some(({ workArea: a }) =>
    pos.x >= a.x - 50 && pos.y >= a.y - 50 && pos.x + width <= a.x + a.width + 50 && pos.y + height <= a.y + a.height + 50
  );
  if (fits) gridWindow.setPosition(pos.x, pos.y);
}

// --- Zasobnik ----------------------------------------------------------------

function createTray() {
  tray = new Tray(nativeImage.createFromPath(path.join(__dirname, 'assets', 'tray.png')));
  tray.setToolTip('Mapa aktywnych okien – Ctrl+Shift + ruch myszą');
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Pokaż mapę', click: showGrid },
      { label: 'Odśwież miniatury', click: () => gridWindow?.webContents.send('tray:refresh') },
      { type: 'separator' },
      { label: 'Zakończ', click: quit },
    ])
  );
  tray.on('click', showGrid);
}

function quit() {
  quitting = true;
  // Backend zapisuje map.json przy zamykaniu; --parent-pid zamknąłby go i tak, ale prosimy grzecznie.
  gridWindow?.webContents.send('app:quitting');
  setTimeout(() => {
    if (backend) backend.kill();
    app.exit(0);
  }, 400);
}

// --- IPC z rendererem ------------------------------------------------------------

ipcMain.handle('app:info', () => ({
  pid: process.pid,
  // HWND okna – backend wysuwa nim okno siatki na pierwszy plan po geście.
  hwnd: gridWindow ? gridWindow.getNativeWindowHandle().readBigUInt64LE(0).toString() : '0',
}));
ipcMain.on('grid:show', showGrid);
ipcMain.on('grid:hide', () => gridWindow?.hide());
ipcMain.on('grid:position', (_e, pos) => applySavedPosition(pos));
ipcMain.on('app:quit', quit);

app.on('window-all-closed', (e) => e.preventDefault());
