using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Ssh;

namespace RootDock.Core.Tests;

/// <summary>
/// Mede tempo: roda sozinho. O SSH.NET espera o handshake bloqueando uma thread do pool; com os
/// outros testes em paralelo, o pool fica sem threads e o aviso de prazo chega atrasado.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class TimingSensitive;

/// <summary>
/// O prazo curto vale até o servidor responder; servidores mudos (ou que travam no meio do
/// handshake) não podem segurar a conexão pelos minutos reservados à confirmação do usuário.
/// </summary>
[Collection(typeof(TimingSensitive))]
public sealed class SshConnectTimeoutTests
{
    [Theory]
    [InlineData(false)] // aceita a conexão e não manda nada
    [InlineData(true)]  // manda o banner SSH e trava antes da troca de chaves
    public async Task SilentServer_FailsWithinTheResponseTimeout(bool sendBanner)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            if (sendBanner)
            {
                await socket.SendAsync(Encoding.ASCII.GetBytes("SSH-2.0-Silencioso\r\n"));
            }

            await Task.Delay(TimeSpan.FromSeconds(20));
        });

        var info = new ConnectionInfo("127.0.0.1", port, "teste", new PasswordAuthenticationMethod("teste", "x"))
        {
            Timeout = SshClientFactory.InteractionTimeout,
        };
        using var client = new SshClient(info);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() =>
            SshClientFactory.ConnectAsync(client, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(1)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"demorou {watch.Elapsed}");
        listener.Stop();
    }

    [Fact]
    public async Task UserCancel_IsNotReportedAsTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await Task.Delay(TimeSpan.FromSeconds(20));
        });

        var info = new ConnectionInfo("127.0.0.1", port, "teste", new PasswordAuthenticationMethod("teste", "x"));
        using var client = new SshClient(info);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => SshClientFactory.ConnectAsync(client, cts.Token, TimeSpan.FromSeconds(10)));
        Assert.IsNotType<SshOperationTimeoutException>(ex);
        listener.Stop();
    }
}
