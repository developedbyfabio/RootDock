using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RootDock.Core.Assistant;

/// <summary>
/// Servidor MCP mínimo (HTTP em 127.0.0.1, porta sorteada, com senha por execução) pelo qual o
/// Claude Code usa as ferramentas do RootDock. Só responde a quem manda a senha, que vai para o
/// Claude Code por variável de ambiente e nunca é gravada em disco.
/// </summary>
public sealed class McpToolServer : IDisposable
{
    public const string ServerName = "rootdock";

    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ToolCall, CancellationToken, Task<ToolResult>> _run;

    public McpToolServer(Func<ToolCall, CancellationToken, Task<ToolResult>> run)
    {
        _run = run;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>Ferramentas que não são oferecidas (quem usa o servidor tem outra no lugar).</summary>
    public Func<string, bool>? Hidden { get; set; }

    /// <summary>Cancela as ferramentas em andamento quando a pessoa interrompe a resposta.</summary>
    public CancellationToken TurnToken { get; set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // servidor encerrado
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                while (await ReadRequestAsync(stream, _stop.Token).ConfigureAwait(false) is { } request)
                {
                    var (status, body) = await HandleAsync(request).ConfigureAwait(false);
                    var payload = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: keep-alive\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, _stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException or
                                           InvalidOperationException or JsonException or FormatException)
            {
                // conexão encerrada ou pedido malformado: nada a responder
            }
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        var end = 0;
        while (end < 4)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            end = one[0] == (end % 2 == 0 ? (byte)'\r' : (byte)'\n') ? end + 1 : one[0] == (byte)'\r' ? 1 : 0;
            if (buffer.Count > 64 * 1024)
            {
                throw new InvalidDataException();
            }
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines.Length > 0 ? lines[0].Split(' ') : [];
        if (first.Length < 2)
        {
            throw new InvalidDataException();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var length = headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
        if (length is < 0 or > MaxBodyBytes)
        {
            throw new InvalidDataException();
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return new Request(first[0], first[1], headers, body);
    }

    private async Task<(string Status, string Body)> HandleAsync(Request request)
    {
        var expected = Encoding.ASCII.GetBytes("Bearer " + Token);
        var given = Encoding.ASCII.GetBytes(request.Headers.GetValueOrDefault("Authorization", ""));
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return ("401 Unauthorized", "{}");
        }

        if (request.Method != "POST")
        {
            // Sem canal de eventos do servidor (GET) nem sessão para encerrar (DELETE).
            return ("405 Method Not Allowed", "{}");
        }

        JsonNode? message;
        try
        {
            message = JsonNode.Parse(request.Body);
        }
        catch (JsonException)
        {
            return ("400 Bad Request", Error(null, -32700, "JSON inválido"));
        }

        if (message is not JsonObject call || call["method"] is not JsonValue named || !named.TryGetValue<string>(out var method))
        {
            return ("400 Bad Request", Error(null, -32600, "Pedido inválido"));
        }

        var id = call["id"];
        if (id is null)
        {
            return ("202 Accepted", ""); // notificação: sem resposta
        }

        switch (method)
        {
            case "initialize":
                return ("200 OK", Result(id, new JsonObject
                {
                    ["protocolVersion"] = call["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1" },
                }));

            case "ping":
                return ("200 OK", Result(id, new JsonObject()));

            case "tools/list":
                return ("200 OK", Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(AssistantTools.Definitions.Where(t => Hidden?.Invoke(t.Name) != true).Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = JsonNode.Parse(t.Schema.GetRawText()),
                    }).ToArray()),
                }));

            case "tools/call":
                var name = call["params"]?["name"]?.GetValue<string>() ?? "";
                var arguments = call["params"]?["arguments"] is { } given2
                    ? JsonSerializer.SerializeToElement(given2)
                    : JsonSerializer.SerializeToElement(new { });
                ToolResult result;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, TurnToken);
                    result = await _run(new ToolCall("mcp_" + Guid.NewGuid().ToString("N")[..12], name, arguments), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = new ToolResult("", "Interrompido pela pessoa.", IsError: true);
                }

                return ("200 OK", Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Content.Length > 0 ? result.Content : "(sem saída)" }),
                    ["isError"] = result.IsError,
                }));

            default:
                return ("200 OK", Error(id, -32601, "Método não suportado: " + method));
        }
    }

    private static string Result(JsonNode id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
