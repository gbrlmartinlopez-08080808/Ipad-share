using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IpadShare.App.App;

namespace IpadShare.App.Calibration;

/// <summary>
/// Minimal HTTP + WebSocket server on the local network for the one-time calibration page.
/// Serves one embedded HTML page; a random token in the URL keeps other devices out.
/// No URL ACL / admin rights needed (raw sockets, not HttpListener).
/// </summary>
internal sealed class CalibrationServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private WebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    public int Port { get; private set; }
    public IPAddress? Address { get; private set; }
    public string? Url => Address == null ? null : $"http://{Address}:{Port}/?k={Token}";
    public bool ClientConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Raised on a background thread for every JSON message from the page.</summary>
    public event Action<JsonElement>? Message;
    public event Action? ClientChanged;

    public void Start()
    {
        Address = FindLanAddress();
        if (Address == null) throw new InvalidOperationException("The laptop isn't connected to a Wi-Fi or Ethernet network.");
        foreach (int port in new[] { 47800, 47801, 47802, 47803, 0 })
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException) { _listener = null; }
        }
        if (_listener == null) throw new InvalidOperationException("Couldn't open a network port.");
        Log.Info($"Calibration server at {Url}");
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private static IPAddress? FindLanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .Select(n => (n, props: n.GetIPProperties()))
            .Where(x => x.props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .SelectMany(x => x.props.UnicastAddresses.Select(u => u.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToList();
        return candidates.FirstOrDefault();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClient(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Warn("Calibration accept: " + ex.Message); }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            var (method, target, headers) = await ReadRequest(stream, ct);
            var uri = new Uri("http://x" + target);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            bool authorised = query["k"] == Token;

            if (method == "GET" && uri.AbsolutePath == "/ws" && authorised &&
                headers.TryGetValue("upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
                headers.TryGetValue("sec-websocket-key", out var key))
            {
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await WriteAscii(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                         $"Sec-WebSocket-Accept: {accept}\r\n\r\n", ct);
                var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
                var old = _socket;
                _socket = ws;
                if (old != null) { try { old.Abort(); } catch { } }
                ClientChanged?.Invoke();
                await ReceiveLoop(ws, ct);
                if (ReferenceEquals(_socket, ws)) { _socket = null; ClientChanged?.Invoke(); }
                return;
            }

            if (method == "GET" && uri.AbsolutePath == "/" && authorised)
            {
                var html = Encoding.UTF8.GetBytes(LoadPage());
                await WriteAscii(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\n" +
                                         "Cache-Control: no-store\r\nConnection: close\r\n\r\n", ct);
                await stream.WriteAsync(html, ct);
                return;
            }

            var body = Encoding.UTF8.GetBytes(authorised ? "Not found" : "This calibration link has expired. Start calibration again on your laptop.");
            await WriteAscii(stream, $"HTTP/1.1 {(authorised ? "404 Not Found" : "403 Forbidden")}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                                     $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n", ct);
            await stream.WriteAsync(body, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException) { }
        catch (Exception ex) { Log.Warn("Calibration client: " + ex.Message); }
    }

    private static string LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("calibrate.html");
        if (s == null) return "<h1>Calibration page missing</h1>";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static async Task<(string method, string target, Dictionary<string, string> headers)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) throw new IOException("closed");
            total += n;
            var text = Encoding.ASCII.GetString(buf, 0, total);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var lines = text[..end].Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2) throw new IOException("bad request");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lines.Skip(1))
                {
                    int c = l.IndexOf(':');
                    if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
                }
                return (first[0], first[1], headers);
            }
            if (total == buf.Length) throw new IOException("request too large");
        }
    }

    private static Task WriteAscii(NetworkStream s, string text, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;
            try
            {
                using var doc = JsonDocument.Parse(sb.ToString());
                Message?.Invoke(doc.RootElement.Clone());
            }
            catch (JsonException) { }
            sb.Clear();
        }
    }

    public async Task SendAsync(object message)
    {
        var ws = _socket;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Warn("Calibration send: " + ex.Message); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        var ws = _socket;
        if (ws != null)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { try { ws.Abort(); } catch { } }
        }
    }
}
