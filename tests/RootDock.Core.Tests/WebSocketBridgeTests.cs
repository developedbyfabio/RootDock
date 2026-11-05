using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using RootDock.Core.Vnc;

namespace RootDock.Core.Tests;

public sealed class WebSocketBridgeTests
{
    [Fact]
    public async Task Bridge_PassesBytesBothWays()
    {
        var ct = TestContext.Current.CancellationToken;
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await echo.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct); // servidor VNC fala primeiro
            var buffer = new byte[64];
            var n = await stream.ReadAsync(buffer, ct);
            await stream.WriteAsync(buffer.AsMemory(0, n), ct); // devolve o que recebeu
            await Task.Delay(200, ct);
        }, ct);

        using var bridge = WebSocketTcpBridge.Start("127.0.0.1", port, "https://rootdock.example");
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("binary");
        ws.Options.SetRequestHeader("Origin", "https://rootdock.example");
        await ws.ConnectAsync(bridge.Url, ct);
        Assert.Equal("binary", ws.SubProtocol);

        var buffer = new byte[64];
        var first = await ws.ReceiveAsync(buffer, ct);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(buffer, 0, first.Count));

        await ws.SendAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), WebSocketMessageType.Binary, true, ct);
        var second = await ws.ReceiveAsync(buffer, ct);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(buffer, 0, second.Count));
        await server;
    }

    [Fact]
    public async Task WrongPath_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var bridge = WebSocketTcpBridge.Start("127.0.0.1", 9, null);
        var wrong = new UriBuilder(bridge.Url) { Path = "/outro" }.Uri;
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(wrong, ct));
    }

    [Fact]
    public async Task OtherOrigin_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var bridge = WebSocketTcpBridge.Start("127.0.0.1", 9, "https://rootdock.example");
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://site-malicioso.example");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(bridge.Url, ct));
    }

    [Fact]
    public async Task UnreachableServer_ClosesWithReason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // porta fechada

        using var bridge = WebSocketTcpBridge.Start("127.0.0.1", closedPort, null);
        var closed = new TaskCompletionSource<string?>();
        bridge.Closed += r => closed.TrySetResult(r);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(bridge.Url, ct);
        var result = await ws.ReceiveAsync(new byte[64], ct);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Contains("recusou", ws.CloseStatusDescription ?? (await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)) ?? "recusou");
    }
}
