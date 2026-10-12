using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapaOkien.Backend.Services;

public sealed class WsClient
{
    public Guid Id { get; } = Guid.NewGuid();
    public required WebSocket Socket { get; init; }
    /// <summary>"frontend", "extension" albo "?" do czasu wiadomości hello.</summary>
    public string Role { get; set; } = "?";
    /// <summary>Dla rozszerzenia: "edge" / "chrome".</summary>
    public string Browser { get; set; } = "";
    public SemaphoreSlim SendGate { get; } = new(1, 1);
}

/// <summary>
/// Serwer WebSocket tylko na 127.0.0.1 (jak WebSocketHub w DocMonitorze), dwukierunkowy.
/// Łączą się z nim: frontend (okno siatki) oraz rozszerzenie Edge/Chrome.
/// </summary>
public sealed class WsServer
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Guid, WsClient> _clients = new();
    private readonly int _port;

    public event Action<WsClient, JsonNode>? Message;
    public event Action<WsClient>? Disconnected;

    public WsServer(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public IEnumerable<WsClient> Clients => _clients.Values;

    public void Start()
    {
        _listener.Start(); // wyjątek „port zajęty” obsługuje Program
        Log.Info($"WebSocket: ws://127.0.0.1:{_port}/");
        _ = AcceptLoopAsync();
        _ = PingLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            _ = HandleClientAsync(ctx);
        }
    }

    // Ruch co < 30 s utrzymuje przy życiu service worker rozszerzenia MV3 (Chrome 116+).
    private async Task PingLoopAsync()
    {
        while (_listener.IsListening)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            foreach (var c in _clients.Values.Where(c => c.Role == "extension"))
                await SendAsync(c, new { type = "ping" });
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        WebSocket socket;
        try { socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch { return; }

        var client = new WsClient { Socket = socket };
        _clients[client.Id] = client;
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

                JsonNode? node = null;
                try { node = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())); }
                catch (JsonException) { /* pomijamy niepoprawną wiadomość */ }
                message.SetLength(0);
                if (node == null) continue;

                try { Message?.Invoke(client, node); }
                catch (Exception ex) { Log.Error($"Obsługa wiadomości {node["type"]}: {ex}"); }
            }
        }
        catch { /* rozłączenie */ }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            socket.Dispose();
            Disconnected?.Invoke(client);
        }
    }

    public async Task SendAsync(WsClient client, object payload)
    {
        if (client.Socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        // WebSocket nie pozwala na równoległe SendAsync – serializujemy wysyłkę per klient.
        await client.SendGate.WaitAsync();
        try { await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { /* rozłączenie obsłuży pętla odbioru */ }
        finally { client.SendGate.Release(); }
    }

    public Task BroadcastAsync(string role, object payload) =>
        Task.WhenAll(_clients.Values.Where(c => c.Role == role).Select(c => SendAsync(c, payload)));

    public void Stop()
    {
        try { _listener.Stop(); } catch { /* zamykanie */ }
    }
}
