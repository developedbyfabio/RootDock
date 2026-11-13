using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Security;

namespace RootDock.Core.Ssh;

/// <summary>
/// Conexão SSH só para comandos (sem terminal), com a mesma verificação de chave do servidor e as
/// mesmas credenciais das outras. Quem abre fecha: <see cref="Dispose"/> desconecta.
/// </summary>
public sealed class SshCommandClient : IDisposable
{
    private readonly List<IDisposable> _owned;

    private SshCommandClient(SshClient client, List<IDisposable> owned)
    {
        Client = client;
        _owned = owned;
    }

    public SshClient Client { get; }

    /// <param name="connectHost">Endereço real (túnel do bastion); null = o próprio servidor.</param>
    public static async Task<SshCommandClient> ConnectAsync(
        string host, int port, SshCredentials credentials, KnownHostsStore knownHosts, ISshInteraction interaction, CancellationToken ct,
        string? connectHost = null, int? connectPort = null)
    {
        var owned = new List<IDisposable>();
        var info = SshClientFactory.CreateConnectionInfo(connectHost ?? host, connectPort ?? port, credentials, interaction, owned);
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var rejected = false;
        SshClientFactory.AttachHostKeyVerification(client, host, port, knownHosts, interaction, () => rejected = true);
        try
        {
            await SshClientFactory.ConnectAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            foreach (var o in owned)
            {
                o.Dispose();
            }

            if (ex is SshConnectionException && rejected)
            {
                throw new HostKeyRejectedException(host);
            }

            throw;
        }

        return new SshCommandClient(client, owned);
    }

    public void Dispose()
    {
        var client = Client;
        var owned = _owned;
        _ = Task.Run(() =>
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception)
            {
                // já caída
            }

            client.Dispose();
            foreach (var o in owned)
            {
                o.Dispose();
            }
        });
    }
}
