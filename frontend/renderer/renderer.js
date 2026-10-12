// Okno siatki: odbiera stan z backendu (WebSocket) i rysuje nody na siatce N×N.
// Klik w nod → przełączenie do okna, przeciągnięcie → zmiana pola, Esc → schowanie.

const gridEl = document.getElementById('grid');
const gridInfoEl = document.getElementById('gridInfo');
const overflowEl = document.getElementById('overflowBanner');
const offlineEl = document.getElementById('offline');
const toastEl = document.getElementById('toast');
const refreshBtn = document.getElementById('refreshBtn');
const chipEdge = document.getElementById('chipEdge');
const chipChrome = document.getElementById('chipChrome');

let socket = null;
let reconnectTimer = null;
let state = null;
let positionApplied = false;
let dragKey = null;

// --- Połączenie z backendem -------------------------------------------------

function connect() {
  socket = new WebSocket(window.mapa.wsUrl);

  socket.addEventListener('open', async () => {
    const info = await window.mapa.info();
    send({ type: 'hello', role: 'frontend', pid: info.pid, hwnd: info.hwnd });
  });
  socket.addEventListener('close', () => {
    offlineEl.hidden = false;
    clearTimeout(reconnectTimer);
    // Backend startuje razem z frontendem – zwykle wystarczy chwilę poczekać.
    reconnectTimer = setTimeout(connect, 1500);
  });
  socket.addEventListener('message', (event) => {
    let msg;
    try {
      msg = JSON.parse(event.data);
    } catch (err) {
      console.error('Niepoprawna wiadomość z backendu:', err);
      return;
    }
    handle(msg);
  });
}

function send(msg) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(msg));
}

function handle(msg) {
  switch (msg.type) {
    case 'state':
      offlineEl.hidden = true;
      state = msg;
      if (!positionApplied && msg.window) {
        positionApplied = true;
        window.mapa.applyPosition(msg.window);
      }
      render();
      break;
    case 'show':
      window.mapa.show();
      break;
    case 'notice':
      showToast(msg.text);
      break;
  }
}

// --- Rysowanie ----------------------------------------------------------------

function render() {
  if (!state) return;
  const { size, cells } = state.grid;
  const nodes = state.nodes;

  gridInfoEl.textContent = `Siatka ${size}×${size} · nodów ${state.total} / pól ${cells}`;
  chipEdge.classList.toggle('chip--on', state.extensions.edge);
  chipChrome.classList.toggle('chip--on', state.extensions.chrome);
  chipEdge.title = state.extensions.edge ? 'Rozszerzenie Edge połączone – karty są nodami' : 'Rozszerzenie Edge niepołączone – okna Edge są nodami';
  chipChrome.title = state.extensions.chrome ? 'Rozszerzenie Chrome połączone – karty są nodami' : 'Rozszerzenie Chrome niepołączone – okna Chrome są nodami';

  overflowEl.hidden = !state.overflowMessage;
  overflowEl.textContent = state.overflowMessage || '';

  gridEl.style.gridTemplateColumns = `repeat(${size}, minmax(0, 1fr))`;
  gridEl.style.gridTemplateRows = `repeat(${size}, minmax(0, 1fr))`;
  gridEl.classList.toggle('grid--empty', nodes.length === 0);

  const byCell = new Map(nodes.map((n) => [`${n.row}:${n.col}`, n]));
  const frag = document.createDocumentFragment();
  for (let row = 0; row < size; row++) {
    for (let col = 0; col < size; col++) {
      const cell = document.createElement('div');
      cell.className = 'cell';
      cell.dataset.row = row;
      cell.dataset.col = col;
      wireDropTarget(cell);
      const node = byCell.get(`${row}:${col}`);
      if (node) cell.appendChild(renderNode(node));
      frag.appendChild(cell);
    }
  }
  gridEl.replaceChildren(frag);
}

function renderNode(n) {
  const el = document.createElement('div');
  el.className = 'node' + (n.kind === 'tab' ? ' node--tab' : '');
  el.draggable = true;
  el.title = `${n.title}\n${n.subtitle}`;
  el.dataset.key = n.key;

  const thumb = document.createElement('div');
  thumb.className = 'node__thumb';
  if (n.thumb) {
    const img = document.createElement('img');
    img.src = n.thumb;
    img.alt = '';
    img.onerror = () => img.replaceWith(placeholder(n));
    thumb.appendChild(img);
  } else {
    thumb.appendChild(placeholder(n));
  }
  el.appendChild(thumb);

  const bar = document.createElement('div');
  bar.className = 'node__bar';
  bar.appendChild(icon(n));
  const text = document.createElement('div');
  text.className = 'node__text';
  const title = document.createElement('div');
  title.className = 'node__title';
  title.textContent = n.title;
  const sub = document.createElement('div');
  sub.className = 'node__sub';
  sub.textContent = n.subtitle;
  text.append(title, sub);
  bar.appendChild(text);
  el.appendChild(bar);

  const badges = [];
  if (n.minimized) badges.push('zminimalizowane');
  if (n.otherDesktop) badges.push('inny pulpit');
  if (badges.length) {
    const box = document.createElement('div');
    box.className = 'node__badges';
    for (const b of badges) {
      const span = document.createElement('span');
      span.className = 'badge';
      span.textContent = b;
      box.appendChild(span);
    }
    el.appendChild(box);
  }

  el.addEventListener('click', () => activate(n.key));
  el.addEventListener('dragstart', (e) => {
    dragKey = n.key;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', n.key);
    requestAnimationFrame(() => el.classList.add('node--dragging'));
  });
  el.addEventListener('dragend', () => {
    dragKey = null;
    el.classList.remove('node--dragging');
    document.querySelectorAll('.cell--drop').forEach((c) => c.classList.remove('cell--drop'));
  });
  return el;
}

/** Kolor wyznaczony z nazwy procesu/domeny – ta sama aplikacja ma zawsze ten sam kolor. */
function hue(text) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.codePointAt(0)) % 360;
  return `hsl(${h}, 45%, 38%)`;
}

function letter(n) {
  const source = n.kind === 'tab' ? n.title : n.subtitle;
  return (source.trim()[0] || '?').toUpperCase();
}

function placeholder(n) {
  const box = document.createElement('div');
  box.className = 'node__placeholder';
  box.style.background = hue(n.subtitle);
  if (n.favicon) {
    const img = document.createElement('img');
    img.src = n.favicon;
    img.alt = '';
    img.onerror = () => img.replaceWith(document.createTextNode(letter(n)));
    box.appendChild(img);
  } else {
    box.textContent = letter(n);
  }
  return box;
}

function icon(n) {
  const box = document.createElement('div');
  box.className = 'node__icon';
  if (n.favicon) {
    const img = document.createElement('img');
    img.src = n.favicon;
    img.alt = '';
    img.onerror = () => {
      box.style.background = hue(n.subtitle);
      box.textContent = letter(n);
    };
    box.appendChild(img);
  } else {
    box.style.background = hue(n.subtitle);
    box.textContent = letter(n);
  }
  return box;
}

// --- Przeciąganie między polami ---------------------------------------------

function wireDropTarget(cell) {
  cell.addEventListener('dragover', (e) => {
    if (!dragKey) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    cell.classList.add('cell--drop');
  });
  cell.addEventListener('dragleave', (e) => {
    if (!cell.contains(e.relatedTarget)) cell.classList.remove('cell--drop');
  });
  cell.addEventListener('drop', (e) => {
    e.preventDefault();
    cell.classList.remove('cell--drop');
    const key = dragKey;
    if (!key || !state) return;
    const row = Number(cell.dataset.row);
    const col = Number(cell.dataset.col);
    moveLocally(key, row, col);
    send({ type: 'move', key, row, col });
  });
}

/** Natychmiastowa zmiana na ekranie (backend potwierdzi stanem; zajęte pole → zamiana). */
function moveLocally(key, row, col) {
  const node = state.nodes.find((n) => n.key === key);
  if (!node || (node.row === row && node.col === col)) return;
  const other = state.nodes.find((n) => n.row === row && n.col === col);
  if (other) {
    other.row = node.row;
    other.col = node.col;
  }
  node.row = row;
  node.col = col;
  render();
}

// --- Akcje ----------------------------------------------------------------------

function activate(key) {
  // Najpierw chowamy okno siatki, żeby nie zasłaniało i nie blokowało przełączenia.
  window.mapa.hide();
  send({ type: 'activate', key });
}

function refreshThumbs() {
  send({ type: 'refreshThumbs' });
  refreshBtn.disabled = true;
  setTimeout(() => (refreshBtn.disabled = false), 1500);
}

let toastTimer = null;
function showToast(text) {
  toastEl.textContent = text;
  toastEl.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (toastEl.hidden = true), 8000);
}

refreshBtn.addEventListener('click', refreshThumbs);
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') window.mapa.hide();
});

window.mapa.onMoved((pos) => send({ type: 'windowMoved', x: pos.x, y: pos.y }));
window.mapa.onTrayRefresh(refreshThumbs);
window.mapa.onQuitting(() => send({ type: 'quit' }));

connect();
