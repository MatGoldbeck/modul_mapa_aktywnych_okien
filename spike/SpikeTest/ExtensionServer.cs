using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpikeTest;

internal sealed record TabInfo(int Id, int WindowId, string Url, string Title, bool Active, double LastAccessed, string Browser);

/// <summary>
/// Serwer WebSocket dla rozszerzenia przeglądarki (tylko 127.0.0.1), wzorowany na
/// WebSocketHub z DocMonitora. W przeciwieństwie do DocMonitora jest dwukierunkowy:
/// rozszerzenie wysyła listę kart i zdarzenia, backend wysyła polecenie „aktywuj kartę”.
/// </summary>
internal sealed class ExtensionServer
{
    public const int Port = 5892; // DocMonitor używa 5891

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Guid, (WebSocket Socket, string Browser)> _clients = new();
    private readonly ConcurrentDictionary<(string Browser, int Id), TabInfo> _tabs = new();

    // WebSocket nie pozwala na równoległe SendAsync – serializujemy wysyłkę.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public bool LogTabEvents { get; set; } = true;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Line($"[ROZSZERZENIE] Nie można nasłuchiwać na porcie {Port}: {ex.Message}");
            return;
        }
        Log.Line($"[ROZSZERZENIE] Nasłuch: ws://127.0.0.1:{Port}/");
        _ = AcceptLoopAsync();
        _ = PingLoopAsync();
    }

    public IReadOnlyList<TabInfo> Tabs => _tabs.Values.OrderBy(t => t.Browser).ThenBy(t => t.WindowId).ThenBy(t => t.Id).ToList();

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            var ctx = await _listener.GetContextAsync();
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            _ = HandleClientAsync(ctx);
        }
    }

    // Ruch co < 30 s utrzymuje service worker rozszerzenia MV3 przy życiu (Chrome 116+).
    private async Task PingLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            foreach (var id in _clients.Keys) await SendAsync(id, new { type = "ping" });
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        WebSocket socket;
        try { socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch { return; }

        var id = Guid.NewGuid();
        _clients[id] = (socket, "?");
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                Handle(id, text);
            }
        }
        catch { /* rozłączenie */ }
        finally
        {
            if (_clients.TryRemove(id, out var c))
            {
                foreach (var key in _tabs.Keys.Where(k => k.Browser == c.Browser)) _tabs.TryRemove(key, out _);
                Log.Line($"[ROZSZERZENIE] Rozłączono: {c.Browser}");
            }
            socket.Dispose();
        }
    }

    private void Handle(Guid client, string text)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(text); }
        catch { return; }
        var type = (string?)msg?["type"];
        var browser = _clients.TryGetValue(client, out var c) ? c.Browser : "?";

        switch (type)
        {
            case "hello":
                browser = (string?)msg!["browser"] ?? "?";
                _clients[client] = (c.Socket, browser);
                Log.Line($"[ROZSZERZENIE] Połączono: {browser} (rozszerzenie {(string?)msg["version"]})");
                break;

            case "tabs":
                foreach (var key in _tabs.Keys.Where(k => k.Browser == browser)) _tabs.TryRemove(key, out _);
                foreach (var t in msg!["tabs"]!.AsArray()) Upsert(browser, t!);
                Log.Line($"[ROZSZERZENIE] {browser}: {msg["tabs"]!.AsArray().Count} kart, " +
                         $"{UniqueUrls()} unikalnych adresów (= nodów)");
                break;

            case "tabEvent":
                var ev = (string?)msg!["event"];
                var tab = msg["tab"]!;
                if (ev == "removed")
                {
                    _tabs.TryRemove((browser, (int)tab["id"]!), out var removed);
                    if (LogTabEvents) Log.Line($"[- KARTA] {browser}: {removed?.Url ?? "?"}");
                }
                else
                {
                    var t = Upsert(browser, tab);
                    if (LogTabEvents && (ev != "updated" || (string?)tab["changed"] == "url"))
                        Log.Line($"[{Symbol(ev)} KARTA] {browser}: {t.Title} — {t.Url}");
                }
                break;

            case "thumb":
                var dataUrl = (string?)msg!["dataUrl"] ?? "";
                SaveTabThumb(browser, (int)msg["tabId"]!, dataUrl);
                break;

            case "error":
                Log.Line($"[ROZSZERZENIE] {browser} błąd: {(string?)msg!["message"]}");
                break;
        }
    }

    private static string Symbol(string? ev) => ev switch { "created" => "+", "activated" => ">", _ => "~" };

    private TabInfo Upsert(string browser, JsonNode t)
    {
        var info = new TabInfo((int)t["id"]!, (int)t["windowId"]!, (string?)t["url"] ?? "", (string?)t["title"] ?? "",
            (bool?)t["active"] ?? false, (double?)t["lastAccessed"] ?? 0, browser);
        _tabs[(browser, info.Id)] = info;
        return info;
    }

    private int UniqueUrls() => _tabs.Values.Select(t => t.Url).Distinct().Count();

    private void SaveTabThumb(string browser, int tabId, string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return;
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        var dir = Path.Combine(Log.OutputDir, "karty");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{browser}_{tabId}.jpg");
        File.WriteAllBytes(path, bytes);
        if (LogTabEvents) Log.Line($"[MINIATURA KARTY] {browser} #{tabId}: {bytes.Length / 1024} KB → {path}");
    }

    /// <summary>Aktywuje kartę; przy zduplikowanym adresie – ostatnio aktywną z nich.</summary>
    public async Task ActivateAsync(TabInfo tab)
    {
        var target = _tabs.Values.Where(t => t.Url == tab.Url).OrderByDescending(t => t.LastAccessed).First();
        var client = _clients.FirstOrDefault(c => c.Value.Browser == target.Browser).Key;
        if (client == Guid.Empty)
        {
            Log.Line("  Brak połączenia z tą przeglądarką.");
            return;
        }
        Log.Line($"  Aktywuję kartę #{target.Id} ({target.Browser}): {target.Title}");
        await SendAsync(client, new { type = "activateTab", tabId = target.Id, windowId = target.WindowId });
        await Task.Delay(500);
        var fg = Native.GetForegroundWindow();
        var w = WindowEnumerator.TryDescribe(fg);
        Log.Line($"  Na pierwszym planie po 0,5 s: [{w?.ProcessName ?? "?"}] {WindowEnumerator.GetTitle(fg)}");
    }

    public async Task RequestTabsAsync()
    {
        foreach (var id in _clients.Keys) await SendAsync(id, new { type = "getTabs" });
    }

    private async Task SendAsync(Guid client, object payload)
    {
        if (!_clients.TryGetValue(client, out var c) || c.Socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendGate.WaitAsync();
        try { await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { /* rozłączenie obsłuży pętla odbioru */ }
        finally { _sendGate.Release(); }
    }
}
