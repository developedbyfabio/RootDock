using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace RootDock.Core.Vnc;

/// <summary>
/// Ponte local de uma conexão só: a tela (noVNC, dentro do WebView2) abre um WebSocket em
/// 127.0.0.1 com um caminho secreto e a ponte repassa os bytes para o servidor VNC por TCP.
/// Só aceita conexões da própria máquina, do caminho certo e da origem da página do RootDock;
/// depois da primeira conexão para de escutar.
/// </summary>
public sealed class WebSocketTcpBridge : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly string _host;
    private readonly int _port;
    private readonly string? _allowedOrigin;
    private readonly CancellationTokenSource _cts = new();

    private WebSocketTcpBridge(string host, int port, string? allowedOrigin)
    {
        _host = host;
        _port = port;
        _allowedOrigin = allowedOrigin;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        _ = AcceptAsync();
    }

    /// <summary>Endereço para o noVNC (ws://127.0.0.1:porta/segredo).</summary>
    public Uri Url => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{_token}");

    /// <summary>A conexão terminou (null = fechada normalmente; texto = motivo).</summary>
    public event Action<string?>? Closed;

    /// <param name="allowedOrigin">Origem da página que pode usar a ponte (ex.: https://rootdock.app); null aceita qualquer.</param>
    public static WebSocketTcpBridge Start(string host, int port, string? allowedOrigin) => new(host, port, allowedOrigin);

    private async Task AcceptAsync()
    {
        TcpClient? page = null;
        TcpClient? server = null;
        string? reason = null;
        try
        {
            // Conexões inválidas (caminho ou origem errados) são recusadas e a ponte continua
            // esperando a da página; depois da válida, para de escutar.
            NetworkStream pageStream;
            string? subProtocol;
            while (true)
            {
                page = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                pageStream = page.GetStream();
                try
                {
                    subProtocol = await HandshakeAsync(pageStream, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException && !_cts.IsCancellationRequested)
                {
                    subProtocol = null;
                }

                if (subProtocol is not null)
                {
                    break;
                }

                page.Dispose();
                page = null;
            }

            _listener.Stop(); // uma conexão por ponte

            using var socket = WebSocket.CreateFromStream(pageStream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = subProtocol.Length == 0 ? null : subProtocol,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            server = new TcpClient { NoDelay = true };
            try
            {
                using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connectLimit.CancelAfter(TimeSpan.FromSeconds(15));
                await server.ConnectAsync(_host, _port, connectLimit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                reason = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                    ? $"{_host}:{_port} recusou a conexão (servidor VNC desligado ou porta errada)."
                    : ex is OperationCanceledException ? $"{_host}:{_port} não respondeu." : ex.Message;
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, Trim(reason), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var serverStream = server.GetStream();
            var toServer = PumpToServerAsync(socket, serverStream, _cts.Token);
            var toPage = PumpToPageAsync(serverStream, socket, _cts.Token);
            await Task.WhenAny(toServer, toPage).ConfigureAwait(false);
            await _cts.CancelAsync().ConfigureAwait(false);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or ObjectDisposedException)
        {
            reason = ex.Message;
        }
        finally
        {
            server?.Dispose();
            page?.Dispose();
            _listener.Stop();
            Closed?.Invoke(reason);
        }
    }

    /// <summary>Lê o pedido HTTP de upgrade e responde 101. Retorna o subprotocolo, ou null se recusado.</summary>
    private async Task<string?> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }

            if (length == buffer.Length)
            {
                await RejectAsync(stream, "431 Request Header Fields Too Large").ConfigureAwait(false);
                return null;
            }
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var headers = lines.Skip(1).TakeWhile(l => l.Length > 0)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (requestLine.Length < 2 || requestLine[0] != "GET" || requestLine[1] != "/" + _token)
        {
            await RejectAsync(stream, "404 Not Found").ConfigureAwait(false);
            return null;
        }

        if (_allowedOrigin is not null && headers.TryGetValue("Origin", out var origin) &&
            !string.Equals(origin, _allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(stream, "403 Forbidden").ConfigureAwait(false);
            return null;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
        {
            await RejectAsync(stream, "400 Bad Request").ConfigureAwait(false);
            return null;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var protocols = headers.TryGetValue("Sec-WebSocket-Protocol", out var p) ? p.Split(',').Select(x => x.Trim()).ToList() : [];
        var chosen = protocols.Contains("binary") ? "binary" : "";
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n" +
                       (chosen.Length > 0 ? $"Sec-WebSocket-Protocol: {chosen}\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        return chosen;
    }

    private static async Task RejectAsync(NetworkStream stream, string status)
    {
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private static async Task PumpToServerAsync(WebSocket socket, NetworkStream server, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            await server.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
        }
    }

    private static async Task PumpToPageAsync(NetworkStream server, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>O motivo do fechamento do WebSocket tem limite de 123 bytes (UTF-8).</summary>
    private static string Trim(string text)
    {
        while (Encoding.UTF8.GetByteCount(text) > 120)
        {
            text = text[..^1];
        }

        return text;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
