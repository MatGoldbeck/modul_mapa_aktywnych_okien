using System.Text.Json.Nodes;
using MapaOkien.Backend.Native;

namespace MapaOkien.Backend.Services;

internal sealed record TabInfo(string Browser, int Id, int WindowId, string Url, string Title, string Favicon, double LastAccessed);

internal sealed class Node
{
    public required string Key { get; set; }
    public required string Kind { get; init; } // "window" | "tab"
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? Favicon { get; set; }
    public bool Minimized { get; set; }
    public bool OtherDesktop { get; set; }
    public int ThumbVersion { get; set; }
}

/// <summary>
/// Serce backendu: z okien (WindowWatcher) i kart (rozszerzenie) wylicza zbiór nodów, układa je
/// na siatce (GridLayout), robi miniatury, zapisuje map.json i wysyła stan do okna siatki.
/// Wszystkie pola chronione jednym zamkiem; operacje Win32, które mogą czekać (przełączanie,
/// miniatury), wykonywane są poza zamkiem.
/// </summary>
public sealed class NodeManager
{
    private readonly object _gate = new();
    private readonly WsServer _ws;
    private readonly Thumbnails _thumbs;
    private readonly GridLayout _layout;
    private readonly MapFile _map;
    private readonly HashSet<string> _excluded;
    private readonly int _screenWidth, _screenHeight;

    private readonly Dictionary<IntPtr, WindowInfo> _windows = new();
    private readonly Dictionary<IntPtr, long> _lastActive = new();
    private readonly Dictionary<(string Browser, int Id), TabInfo> _tabs = new();
    private readonly HashSet<string> _connectedBrowsers = new();
    private readonly Dictionary<string, Node> _nodes = new();
    private readonly Dictionary<IntPtr, DateTime> _lastCapture = new();

    private IntPtr _frontendHwnd;
    private IntPtr _previousForeground;
    private string? _pendingNotice;
    private bool _otherBrowserNoticeSent;
    private long _activationCounter;

    private readonly System.Threading.Timer _broadcastTimer;
    private readonly System.Threading.Timer _saveTimer;

    public NodeManager(WsServer ws, Thumbnails thumbs, MapFile map, ConfigFile config, int screenWidth, int screenHeight)
    {
        _ws = ws;
        _thumbs = thumbs;
        _map = map;
        _excluded = new HashSet<string>(config.ExcludedProcesses, StringComparer.OrdinalIgnoreCase);
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _layout = new GridLayout(GridSize.For(screenWidth, screenHeight), map.Cells);
        _broadcastTimer = new System.Threading.Timer(_ => _ = BroadcastStateAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer = new System.Threading.Timer(_ => SaveMap(), null, Timeout.Infinite, Timeout.Infinite);

        ws.Message += OnMessage;
        ws.Disconnected += OnDisconnected;
    }

    public int GridSizeValue => _layout.Size;

    // =====================================================================
    // Okna (WindowWatcher)
    // =====================================================================

    public void Attach(WindowWatcher watcher)
    {
        watcher.Added += w =>
        {
            lock (_gate)
            {
                _windows[w.Hwnd] = w;
                Recompute();
            }
            if (WindowEnumerator.OtherBrowsers.Contains(w.ProcessName)) NoticeOtherBrowser(w.ProcessName);
            ThreadPool.QueueUserWorkItem(_ => CaptureWindow(w.Hwnd));
        };
        watcher.Removed += w =>
        {
            lock (_gate)
            {
                _windows.Remove(w.Hwnd);
                _lastActive.Remove(w.Hwnd);
                _lastCapture.Remove(w.Hwnd);
                Recompute();
            }
        };
        watcher.Updated += (before, after) =>
        {
            lock (_gate)
            {
                _windows[after.Hwnd] = after;
                Recompute(before.Key != after.Key ? (before.Key, after.Key) : null);
            }
        };
        watcher.Foreground += hwnd =>
        {
            IntPtr previous;
            lock (_gate)
            {
                _lastActive[hwnd] = ++_activationCounter;
                previous = _previousForeground;
                _previousForeground = hwnd;
            }
            // Okno, które użytkownik właśnie opuszcza, ma najbardziej aktualny wygląd – odświeżamy je.
            if (previous != IntPtr.Zero && previous != hwnd && previous != _frontendHwnd)
                ThreadPool.QueueUserWorkItem(_ => CaptureWindow(previous, throttle: true));
        };
        watcher.Gesture += OnGesture;
    }

    // =====================================================================
    // Wyliczanie nodów
    // =====================================================================

    /// <summary>
    /// Porównuje pożądany zbiór nodów (z okien i kart) z bieżącym. Wywoływać pod zamkiem.
    /// <paramref name="rename"/>: zmiana klucza tego samego okna/karty (tytuł, adres) – nod zostaje w polu.
    /// </summary>
    private void Recompute((string Old, string New)? rename = null)
    {
        var desired = new Dictionary<string, Node>();
        var desiredEdge = new Dictionary<string, Edge>();

        foreach (var w in _windows.Values)
        {
            if (_excluded.Contains(w.ProcessName)) continue;
            // Gdy rozszerzenie danej przeglądarki jest połączone, nodami są jej karty, nie okna.
            if (WindowEnumerator.SupportedBrowsers.TryGetValue(w.ProcessName, out var b) && _connectedBrowsers.Contains(b))
                continue;
            if (desired.TryGetValue(w.Key, out var existing))
            {
                existing.Minimized &= w.Minimized;
                continue;
            }
            desired[w.Key] = new Node
            {
                Key = w.Key, Kind = "window", Title = w.Title, Subtitle = w.ProcessName,
                Minimized = w.Minimized, OtherDesktop = w.OnCurrentDesktop == false,
            };
            desiredEdge[w.Key] = WindowEnumerator.EdgeFor(w.ProcessName);
        }

        foreach (var group in _tabs.Values.Where(t => !string.IsNullOrEmpty(t.Url)).GroupBy(t => t.Url))
        {
            var latest = group.OrderByDescending(t => t.LastAccessed).First();
            var key = "url:" + group.Key;
            desired[key] = new Node
            {
                Key = key, Kind = "tab", Title = string.IsNullOrEmpty(latest.Title) ? group.Key : latest.Title,
                Subtitle = string.Join(" + ", group.Select(t => t.Browser).Distinct()) + " · " + Host(group.Key),
                Favicon = string.IsNullOrEmpty(latest.Favicon) ? null : latest.Favicon,
            };
            desiredEdge[key] = Edge.Top;
        }

        if (rename is { } r && _nodes.ContainsKey(r.Old) && !desired.ContainsKey(r.Old)
            && desired.ContainsKey(r.New) && !_nodes.ContainsKey(r.New) && _layout.Rename(r.Old, r.New))
        {
            var node = _nodes[r.Old];
            _nodes.Remove(r.Old);
            node.Key = r.New;
            _nodes[r.New] = node;
            MoveThumb(r.Old, r.New);
        }

        foreach (var key in _nodes.Keys.Where(k => !desired.ContainsKey(k)).ToList())
        {
            _nodes.Remove(key);
            _layout.Remove(key);
            _thumbs.Delete(key);
        }

        foreach (var (key, d) in desired)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                node.Title = d.Title;
                node.Subtitle = d.Subtitle;
                node.Favicon = d.Favicon;
                node.Minimized = d.Minimized;
                node.OtherDesktop = d.OtherDesktop;
            }
            else
            {
                d.ThumbVersion = File.Exists(_thumbs.PathFor(key)) ? 1 : 0;
                _nodes[key] = d;
                _layout.Add(key, desiredEdge[key]);
            }
        }

        ScheduleSave();
        ScheduleBroadcast();
    }

    private void MoveThumb(string oldKey, string newKey)
    {
        try
        {
            var from = _thumbs.PathFor(oldKey);
            if (File.Exists(from)) File.Move(from, _thumbs.PathFor(newKey), overwrite: true);
        }
        catch (IOException) { /* miniatura zostanie zrobiona ponownie */ }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // =====================================================================
    // Miniatury
    // =====================================================================

    private void CaptureWindow(IntPtr hwnd, bool throttle = false)
    {
        string key;
        lock (_gate)
        {
            if (!_windows.TryGetValue(hwnd, out var w) || !_nodes.ContainsKey(w.Key)) return;
            if (throttle && _lastCapture.TryGetValue(hwnd, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(2))
                return;
            _lastCapture[hwnd] = DateTime.UtcNow;
            key = w.Key;
        }
        if (!_thumbs.CaptureWindow(hwnd, key)) return;
        lock (_gate)
        {
            if (_nodes.TryGetValue(key, out var node)) node.ThumbVersion++;
        }
        ScheduleBroadcast();
    }

    /// <summary>
    /// Usuwa miniatury okien zamkniętych, gdy program nie działał. Wołane z opóźnieniem, żeby
    /// rozszerzenia zdążyły przysłać karty (ich miniatury też należą do nodów).
    /// </summary>
    public void CleanupOrphanThumbnails()
    {
        HashSet<string> keep;
        lock (_gate) keep = _nodes.Keys.Select(k => Path.GetFileName(_thumbs.PathFor(k))).ToHashSet();
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(AppPaths.Thumbs, "*.webp"))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); removed++; }
            catch (IOException) { /* w użyciu – spróbujemy przy kolejnym starcie */ }
        }
        if (removed > 0) Log.Info($"Usunięto {removed} miniatur nieistniejących już okien");
    }

    public void RefreshAllThumbnails()
    {
        List<IntPtr> windows;
        lock (_gate)
            windows = _windows.Values.Where(w => _nodes.ContainsKey(w.Key)).Select(w => w.Hwnd).ToList();
        foreach (var hwnd in windows) CaptureWindow(hwnd);
        // Karty: przeglądarka pozwala uchwycić tylko aktywną kartę każdego okna.
        _ = _ws.BroadcastAsync("extension", new { type = "captureActiveTabs" });
        Log.Info($"Odświeżono miniatury ({windows.Count} okien)");
    }

    // =====================================================================
    // Gest, przełączanie
    // =====================================================================

    private void OnGesture()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg != _frontendHwnd) CaptureWindow(fg);

        var frontend = _ws.Clients.FirstOrDefault(c => c.Role == "frontend");
        if (frontend == null)
        {
            Log.Error("Gest wykryty, ale okno siatki nie jest połączone");
            return;
        }
        _ = _ws.SendAsync(frontend, new { type = "show" });
        // Frontend sam nie zawsze przebije blokadę pierwszego planu – pomaga backend.
        Thread.Sleep(120);
        if (_frontendHwnd != IntPtr.Zero) WindowSwitcher.BringToFront(_frontendHwnd);
    }

    private async Task ActivateAsync(string key)
    {
        Node? node;
        IntPtr hwnd = IntPtr.Zero;
        TabInfo? tab = null;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(key, out node)) return;
            if (node.Kind == "window")
            {
                // Kilka okien z tym samym kluczem → ostatnio aktywne.
                hwnd = _windows.Values.Where(w => w.Key == key)
                    .OrderByDescending(w => _lastActive.GetValueOrDefault(w.Hwnd)).Select(w => w.Hwnd).FirstOrDefault();
            }
            else
            {
                // Kilka kart z tym samym adresem → ostatnio aktywna.
                tab = _tabs.Values.Where(t => "url:" + t.Url == key).OrderByDescending(t => t.LastAccessed).FirstOrDefault();
            }
        }

        if (hwnd != IntPtr.Zero)
        {
            WindowSwitcher.BringToFront(hwnd);
            return;
        }
        if (tab == null) return;

        var ext = _ws.Clients.FirstOrDefault(c => c.Role == "extension" && c.Browser == tab.Browser);
        if (ext == null) return;
        await _ws.SendAsync(ext, new { type = "activateTab", tabId = tab.Id, windowId = tab.WindowId });
        await Task.Delay(400);

        // chrome.windows.update(focused) nie zawsze przebija blokadę pierwszego planu (test 2026-10-12),
        // więc backend sam wysuwa okno przeglądarki z tytułem aktywowanej karty.
        var process = tab.Browser == "edge" ? "msedge" : "chrome";
        var fg = WindowEnumerator.TryDescribe(Win32.GetForegroundWindow());
        if (fg != null && fg.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase) && fg.Title.StartsWith(tab.Title))
            return;
        IntPtr browserWindow;
        lock (_gate)
            browserWindow = _windows.Values.FirstOrDefault(w =>
                w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase) && w.Title.StartsWith(tab.Title))?.Hwnd ?? IntPtr.Zero;
        if (browserWindow == IntPtr.Zero)
            browserWindow = WindowEnumerator.List().FirstOrDefault(w =>
                w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase) && w.Title.StartsWith(tab.Title))?.Hwnd ?? IntPtr.Zero;
        if (browserWindow != IntPtr.Zero) WindowSwitcher.BringToFront(browserWindow);
    }

    // =====================================================================
    // Wiadomości WebSocket
    // =====================================================================

    private void OnMessage(WsClient client, JsonNode msg)
    {
        var type = (string?)msg["type"];
        if (type == "hello")
        {
            if ((string?)msg["role"] == "frontend") HelloFrontend(client, msg);
            else HelloExtension(client, msg);
            return;
        }

        if (client.Role == "frontend") OnFrontendMessage(type, msg);
        else if (client.Role == "extension") OnExtensionMessage(client, type, msg);
    }

    private void HelloFrontend(WsClient client, JsonNode msg)
    {
        client.Role = "frontend";
        if ((long?)msg["pid"] is { } pid) WindowEnumerator.OwnProcessIds[(uint)pid] = 0;
        if (long.TryParse((string?)msg["hwnd"], out var hwnd)) _frontendHwnd = new IntPtr(hwnd);
        Log.Info("Okno siatki połączone");
        lock (_gate)
        {
            // Okno siatki mogło zostać zarejestrowane jako zwykłe okno, zanim przyszło hello.
            foreach (var w in _windows.Values.Where(w => w.Hwnd == _frontendHwnd).ToList()) _windows.Remove(w.Hwnd);
            Recompute();
        }
        _ = BroadcastStateAsync();
        if (_pendingNotice != null)
        {
            _ = _ws.SendAsync(client, new { type = "notice", text = _pendingNotice });
            _pendingNotice = null;
        }
    }

    private void OnFrontendMessage(string? type, JsonNode msg)
    {
        switch (type)
        {
            case "activate":
                var key = (string?)msg["key"];
                if (key != null) _ = Task.Run(() => ActivateAsync(key));
                break;

            case "move":
                lock (_gate)
                {
                    if (_layout.Move((string)msg["key"]!, new Cell((int)msg["row"]!, (int)msg["col"]!)))
                    {
                        ScheduleSave();
                    }
                }
                ScheduleBroadcast(); // także po odrzuconym ruchu – frontend wraca do stanu z backendu
                break;

            case "refreshThumbs":
                ThreadPool.QueueUserWorkItem(_ => RefreshAllThumbnails());
                break;

            case "windowMoved":
                lock (_gate) _map.Window = new WindowPosition { X = (int)msg["x"]!, Y = (int)msg["y"]! };
                ScheduleSave();
                break;

            case "quit":
                Log.Info("Zakończenie na żądanie okna siatki");
                Program.RequestExit();
                break;
        }
    }

    private void HelloExtension(WsClient client, JsonNode msg)
    {
        client.Role = "extension";
        client.Browser = (string?)msg["browser"] ?? "?";
        Log.Info($"Rozszerzenie połączone: {client.Browser} {(string?)msg["version"]}");
        lock (_gate)
        {
            _connectedBrowsers.Add(client.Browser);
            Recompute();
        }
    }

    private void OnExtensionMessage(WsClient client, string? type, JsonNode msg)
    {
        var browser = client.Browser;
        switch (type)
        {
            case "tabs":
                lock (_gate)
                {
                    foreach (var k in _tabs.Keys.Where(k => k.Browser == browser).ToList()) _tabs.Remove(k);
                    foreach (var t in msg["tabs"]!.AsArray()) Upsert(browser, t!);
                    Recompute();
                }
                break;

            case "tabEvent":
                var ev = (string?)msg["event"];
                var tabNode = msg["tab"]!;
                lock (_gate)
                {
                    var id = (browser, (int)tabNode["id"]!);
                    if (ev == "removed")
                    {
                        _tabs.Remove(id);
                        Recompute();
                    }
                    else
                    {
                        _tabs.TryGetValue(id, out var before);
                        var after = Upsert(browser, tabNode);
                        Recompute(before != null && before.Url != after.Url
                            ? ("url:" + before.Url, "url:" + after.Url) : null);
                    }
                }
                break;

            case "thumb":
                var url = (string?)msg["url"];
                var dataUrl = (string?)msg["dataUrl"];
                if (url == null || dataUrl == null) break;
                var key = "url:" + url;
                lock (_gate)
                    if (!_nodes.ContainsKey(key)) break;
                if (_thumbs.SaveTabImage(dataUrl, key))
                {
                    lock (_gate)
                        if (_nodes.TryGetValue(key, out var n)) n.ThumbVersion++;
                    ScheduleBroadcast();
                }
                break;

            case "error":
                Log.Info($"Rozszerzenie {browser}: {(string?)msg["message"]}");
                break;
        }
    }

    private TabInfo Upsert(string browser, JsonNode t)
    {
        var info = new TabInfo(browser, (int)t["id"]!, (int)t["windowId"]!, (string?)t["url"] ?? "",
            (string?)t["title"] ?? "", (string?)t["favIconUrl"] ?? "", (double?)t["lastAccessed"] ?? 0);
        _tabs[(browser, info.Id)] = info;
        return info;
    }

    private void OnDisconnected(WsClient client)
    {
        if (client.Role == "frontend")
        {
            Log.Info("Okno siatki rozłączone");
            return;
        }
        if (client.Role != "extension") return;
        Log.Info($"Rozszerzenie rozłączone: {client.Browser}");
        lock (_gate)
        {
            // Inna instancja tej samej przeglądarki (np. drugi profil) może nadal być połączona.
            if (_ws.Clients.Any(c => c.Role == "extension" && c.Browser == client.Browser)) return;
            _connectedBrowsers.Remove(client.Browser);
            foreach (var k in _tabs.Keys.Where(k => k.Browser == client.Browser).ToList()) _tabs.Remove(k);
            Recompute();
        }
    }

    private void NoticeOtherBrowser(string process)
    {
        string text;
        lock (_gate)
        {
            // Jeden komunikat na sesję, niezależnie od tego, która z nieobsługiwanych przeglądarek.
            if (_otherBrowserNoticeSent) return;
            _otherBrowserNoticeSent = true;
            text = "W modul_mapa_aktywnych_okien obsługiwane są tylko przeglądarki Edge, Chrome.";
        }
        Log.Info($"{text} (wykryto: {process})");
        if (_ws.Clients.Any(c => c.Role == "frontend"))
            _ = _ws.BroadcastAsync("frontend", new { type = "notice", text });
        else
            _pendingNotice = text;
    }

    // =====================================================================
    // Stan dla frontendu, zapis
    // =====================================================================

    private void ScheduleBroadcast() => _broadcastTimer.Change(80, Timeout.Infinite);

    private void ScheduleSave() => _saveTimer.Change(1000, Timeout.Infinite);

    private Task BroadcastStateAsync()
    {
        object state;
        lock (_gate)
        {
            var nodes = _layout.Placed.Select(p =>
            {
                var n = _nodes[p.Key];
                var thumbPath = _thumbs.PathFor(n.Key);
                return new
                {
                    key = n.Key,
                    kind = n.Kind,
                    title = n.Title,
                    subtitle = n.Subtitle,
                    favicon = n.Favicon,
                    minimized = n.Minimized,
                    otherDesktop = n.OtherDesktop,
                    row = p.Cell.Row,
                    col = p.Cell.Col,
                    thumb = n.ThumbVersion > 0 && File.Exists(thumbPath)
                        ? new Uri(thumbPath).AbsoluteUri + "?v=" + n.ThumbVersion : null,
                };
            }).ToList();

            int total = _nodes.Count;
            state = new
            {
                type = "state",
                grid = new { size = _layout.Size, cells = _layout.Cells, screenWidth = _screenWidth, screenHeight = _screenHeight },
                nodes,
                total,
                overflowMessage = GridSize.OverflowMessage(total, _layout.Cells),
                extensions = new { edge = _connectedBrowsers.Contains("edge"), chrome = _connectedBrowsers.Contains("chrome") },
                window = _map.Window,
            };
        }
        return _ws.BroadcastAsync("frontend", state);
    }

    public void SaveMap()
    {
        try
        {
            lock (_gate)
            {
                _map.Cells = new Dictionary<string, SavedCell>(_layout.Remembered);
                Storage.Save(AppPaths.Map, _map);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Zapis map.json: {ex.Message}");
        }
    }
}
