// Most między przeglądarką (Edge/Chrome) a lokalnym backendem modułu.
// Tylko 127.0.0.1. Karty incognito/InPrivate nigdy nie są wysyłane.

const ENDPOINT = "ws://127.0.0.1:5892/"; // modul_mapa_aktywnych_okien (DocMonitor 5891 – w kolejnej iteracji)
const VERSION = chrome.runtime.getManifest().version;
const BROWSER = navigator.userAgent.includes("Edg/") ? "edge" : "chrome";

let socket = null;
let reconnectTimer = null;

function tabDto(t, changed) {
  return {
    id: t.id,
    windowId: t.windowId,
    url: t.url || t.pendingUrl || "",
    title: t.title || "",
    favIconUrl: t.favIconUrl || "",
    active: !!t.active,
    lastAccessed: t.lastAccessed || 0,
    changed,
  };
}

function send(msg) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(msg));
}

async function sendAllTabs() {
  const tabs = await chrome.tabs.query({});
  send({ type: "tabs", tabs: tabs.filter((t) => !t.incognito).map((t) => tabDto(t)) });
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  socket = new WebSocket(ENDPOINT);
  socket.onopen = () => {
    send({ type: "hello", browser: BROWSER, version: VERSION });
    sendAllTabs();
  };
  socket.onmessage = (e) => handle(JSON.parse(e.data));
  socket.onclose = () => {
    socket = null;
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connect, 3000);
  };
  socket.onerror = () => {}; // onclose zajmie się ponownym połączeniem
}

async function handle(msg) {
  switch (msg.type) {
    case "ping":
      send({ type: "pong" }); // ruch co < 30 s utrzymuje service worker przy życiu
      break;
    case "getTabs":
      sendAllTabs();
      break;
    case "captureActiveTabs":
      captureActiveTabs();
      break;
    case "activateTab":
      try {
        await chrome.tabs.update(msg.tabId, { active: true });
        await chrome.windows.update(msg.windowId, { focused: true, drawAttention: false });
      } catch (err) {
        send({ type: "error", message: `activateTab: ${err.message}` });
      }
      break;
  }
}

// Miniatura da się zrobić tylko dla aktywnej (widocznej) karty okna.
let captureTimer = null;
function scheduleCapture(tab) {
  clearTimeout(captureTimer);
  captureTimer = setTimeout(async () => {
    try {
      const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, { format: "jpeg", quality: 60 });
      send({ type: "thumb", tabId: tab.id, url: tab.url || "", dataUrl });
    } catch (err) {
      // np. strony chrome:// / edge:// lub sklep z rozszerzeniami – brak dostępu
      send({ type: "error", message: `captureVisibleTab (${tab.url}): ${err.message}` });
    }
  }, 400);
}

// „Odśwież miniatury”: aktywna karta każdego okna (Chrome pozwala na ok. 2 przechwycenia/s).
async function captureActiveTabs() {
  const tabs = await chrome.tabs.query({ active: true, windowType: "normal" });
  for (const t of tabs.filter((t) => !t.incognito)) {
    try {
      const dataUrl = await chrome.tabs.captureVisibleTab(t.windowId, { format: "jpeg", quality: 60 });
      send({ type: "thumb", tabId: t.id, url: t.url || "", dataUrl });
    } catch (err) {
      send({ type: "error", message: `captureVisibleTab (${t.url}): ${err.message}` });
    }
    await new Promise((r) => setTimeout(r, 600));
  }
}

chrome.tabs.onCreated.addListener((t) => {
  if (!t.incognito) send({ type: "tabEvent", event: "created", tab: tabDto(t) });
});
chrome.tabs.onRemoved.addListener((tabId) => {
  send({ type: "tabEvent", event: "removed", tab: { id: tabId } });
});
chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  const t = await chrome.tabs.get(tabId).catch(() => null);
  if (!t || t.incognito) return;
  send({ type: "tabEvent", event: "activated", tab: tabDto(t) });
  scheduleCapture(t);
});
chrome.tabs.onUpdated.addListener((tabId, change, t) => {
  if (t.incognito) return;
  if (change.url || change.title) {
    send({ type: "tabEvent", event: "updated", tab: tabDto(t, change.url ? "url" : "title") });
  }
  if (change.status === "complete" && t.active) scheduleCapture(t);
});

// Awaryjne wybudzanie service workera i ponowne połączenie.
chrome.alarms.create("reconnect", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => connect());
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
