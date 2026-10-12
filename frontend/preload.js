const { contextBridge, ipcRenderer } = require('electron');

// Renderer łączy się z backendem bezpośrednio przez WebSocket (127.0.0.1); przez IPC idzie
// tylko to, co dotyczy okna i zasobnika (pokaż/schowaj, pozycja, zakończenie).
contextBridge.exposeInMainWorld('mapa', {
  wsUrl: 'ws://127.0.0.1:5892/',
  info: () => ipcRenderer.invoke('app:info'),
  show: () => ipcRenderer.send('grid:show'),
  hide: () => ipcRenderer.send('grid:hide'),
  applyPosition: (pos) => ipcRenderer.send('grid:position', pos),
  quit: () => ipcRenderer.send('app:quit'),
  onMoved: (cb) => ipcRenderer.on('grid:moved', (_e, pos) => cb(pos)),
  onShown: (cb) => ipcRenderer.on('grid:shown', () => cb()),
  onTrayRefresh: (cb) => ipcRenderer.on('tray:refresh', () => cb()),
  onQuitting: (cb) => ipcRenderer.on('app:quitting', () => cb()),
});
