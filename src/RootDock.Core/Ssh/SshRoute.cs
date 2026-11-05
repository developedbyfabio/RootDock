using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Security;

namespace RootDock.Core.Ssh;

/// <summary>Um bastion no caminho até o servidor (ProxyJump).</summary>
public sealed record SshHop(string Name, string Host, int Port, SshCredentials Credentials);

/// <summary>Falha num bastion: a mensagem diz qual, para não confundir com o servidor final.</summary>
public sealed class JumpHostException(SshHop hop, Exception inner)
    : Exception($"Bastion \"{hop.Name}\" ({hop.Credentials.Username}@{hop.Host}:{hop.Port}): {inner.Message}", inner)
{
    public SshHop Hop { get; } = hop;
}

/// <summary>
/// Caminho até o servidor (ProxyJump / bastion host). Conecta cada bastion em ordem e abre,
/// em cada um, um redirecionamento local (só em 127.0.0.1) até o próximo salto. O terminal, o
/// SFTP e o monitor conectam em <see cref="ConnectHost"/>:<see cref="ConnectPort"/>; a chave do
/// servidor continua sendo verificada com o nome real do destino.
/// Sem bastions, é só o endereço do próprio servidor.
/// </summary>
public sealed class SshRoute : IDisposable
{
    private readonly List<SshClient> _clients = [];
    private readonly List<ForwardedPortLocal> _forwards = [];
    private readonly List<IDisposable> _owned = [];

    private SshRoute(string host, int port)
    {
        ConnectHost = host;
        ConnectPort = port;
    }

    public string ConnectHost { get; private set; }

    public int ConnectPort { get; private set; }

    public int HopCount => _clients.Count;

    /// <summary>Algum bastion caiu (o caminho deixou de funcionar).</summary>
    public bool IsBroken => _clients.Any(c => !c.IsConnected);

    public static SshRoute Direct(string host, int port) => new(host, port);

    public static async Task<SshRoute> OpenAsync(
        IReadOnlyList<SshHop> jumps, string targetHost, int targetPort,
        KnownHostsStore knownHosts, ISshInteraction interaction, CancellationToken ct)
    {
        var route = new SshRoute(targetHost, targetPort);
        if (jumps.Count == 0)
        {
            return route;
        }

        try
        {
            var connectHost = jumps[0].Host;
            var connectPort = jumps[0].Port;
            for (var i = 0; i < jumps.Count; i++)
            {
                var hop = jumps[i];
                var (nextHost, nextPort) = i + 1 < jumps.Count ? (jumps[i + 1].Host, jumps[i + 1].Port) : (targetHost, targetPort);
                try
                {
                    var info = SshClientFactory.CreateConnectionInfo(connectHost, connectPort, hop.Credentials, interaction, route._owned);
                    var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                    var rejected = false;
                    SshClientFactory.AttachHostKeyVerification(client, hop.Host, hop.Port, knownHosts, interaction, () => rejected = true);
                    route._clients.Add(client);
                    try
                    {
                        await SshClientFactory.ConnectAsync(client, ct).ConfigureAwait(false);
                    }
                    catch (SshConnectionException) when (rejected)
                    {
                        throw new HostKeyRejectedException(hop.Host);
                    }

                    // Porta livre escolhida antes (o SSH.NET precisa de um número fixo para escutar).
                    var localPort = FreeLoopbackPort();
                    var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)localPort, nextHost, (uint)nextPort);
                    client.AddForwardedPort(forward);
                    forward.Start();
                    route._forwards.Add(forward);
                    connectHost = IPAddress.Loopback.ToString();
                    connectPort = localPort;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not JumpHostException)
                {
                    throw new JumpHostException(hop, ex);
                }
            }

            route.ConnectHost = connectHost;
            route.ConnectPort = connectPort;
            return route;
        }
        catch
        {
            route.Dispose();
            throw;
        }
    }

    internal static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        // Do último salto para o primeiro: cada túnel depende do anterior.
        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            if (i < _forwards.Count)
            {
                try
                {
                    _forwards[i].Stop();
                }
                catch (Exception)
                {
                    // Encerramento: o bastion pode já ter caído.
                }

                _forwards[i].Dispose();
            }

            try
            {
                _clients[i].Disconnect();
            }
            catch (Exception)
            {
            }

            _clients[i].Dispose();
        }

        _forwards.Clear();
        _clients.Clear();
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _owned.Clear();
    }
}
