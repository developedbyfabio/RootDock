using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Security;
using RootDock.Core.Terminal;

namespace RootDock.Core.Ssh;

public sealed record SshCredentials(string Username, string? Password, string? PrivateKeyPath, string? KeyPassphrase, bool UseAgent = false);

/// <summary>Decisões que dependem do usuário durante a conexão. Chamadas em thread de fundo; podem bloquear.</summary>
public interface ISshInteraction
{
    /// <summary>Servidor desconhecido ou com chave alterada. Retorna true para aceitar.</summary>
    bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob);

    /// <summary>Pergunta extra do servidor (ex.: código 2FA). Retorna null para cancelar.</summary>
    string? AnswerPrompt(string host, string instruction, string prompt, bool echo);
}

public sealed class HostKeyRejectedException(string host)
    : Exception($"A chave do servidor {host} não foi aceita. Conexão cancelada.");

/// <summary>Chave privada protegida por senha e nenhuma senha informada.</summary>
public sealed class PrivateKeyPassphraseRequiredException(string path, Exception inner)
    : Exception($"A chave privada '{path}' está protegida por senha.", inner);

public sealed class SshTerminalConnection : ITerminalConnection
{
    private const string TerminalType = "xterm-256color";

    private readonly KnownHostsStore _knownHosts;
    private readonly ISshInteraction _interaction;
    private readonly OutboundQueue _outbound = new();
    private readonly List<IDisposable> _owned = [];

    private SshClient? _client;
    private SshRoute? _route;
    private ShellStream? _shell;
    private Thread? _reader;
    private bool _hostKeyRejected;
    private int _closed;
    private string? _closeReason;

    public SshTerminalConnection(
        string host,
        int port,
        SshCredentials credentials,
        KnownHostsStore knownHosts,
        ISshInteraction interaction,
        IReadOnlyList<SshHop>? jumps = null)
    {
        Host = host;
        Port = port;
        Credentials = credentials;
        Jumps = jumps ?? [];
        _knownHosts = knownHosts;
        _interaction = interaction;
    }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string?>? Closed;

    public string Host { get; }

    public int Port { get; }

    /// <summary>Bastions até o servidor (ProxyJump), na ordem em que são conectados.</summary>
    public IReadOnlyList<SshHop> Jumps { get; }

    /// <summary>Endereço real de conexão: o túnel local do bastion, ou o próprio servidor.</summary>
    public string ConnectHost => _route?.ConnectHost ?? Host;

    public int ConnectPort => _route?.ConnectPort ?? Port;

    /// <summary>Credenciais que funcionaram; reutilizadas para abrir o SFTP sem perguntar de novo.</summary>
    public SshCredentials Credentials { get; }

    public KnownHostsStore KnownHosts => _knownHosts;

    public ISshInteraction Interaction => _interaction;

    public bool IsConnected => _client?.IsConnected == true && _closed == 0;

    /// <summary>Cliente SSH conectado, para recursos que usam a mesma conexão (monitor, túneis).</summary>
    public SshClient? Client => _client;

    public async Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        _route = await SshRoute.OpenAsync(Jumps, Host, Port, _knownHosts, _interaction, cancellationToken).ConfigureAwait(false);
        var info = SshClientFactory.CreateConnectionInfo(_route.ConnectHost, _route.ConnectPort, Credentials, _interaction, _owned);
        var client = new SshClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        SshClientFactory.AttachHostKeyVerification(client, Host, Port, _knownHosts, _interaction, () => _hostKeyRejected = true);
        client.ErrorOccurred += (_, e) => Close(e.Exception.Message);
        _client = client;

        try
        {
            await SshClientFactory.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (_hostKeyRejected)
        {
            throw new HostKeyRejectedException(Host);
        }

        _shell = client.CreateShellStream(TerminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
        _outbound.Start(
            write: data =>
            {
                _shell.Write(data, 0, data.Length);
                _shell.Flush();
            },
            resize: (c, r) => _shell.ChangeWindowSize((uint)c, (uint)r, 0, 0),
            onError: ex => Close(ex.Message));
    }

    public void Start()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            Closed?.Invoke(_closeReason);
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ssh-read {Host}" };
        _reader.Start();
    }

    public void Write(ReadOnlySpan<byte> data) => _outbound.EnqueueData(data);

    public void Resize(int columns, int rows)
    {
        if (columns > 0 && rows > 0)
        {
            _outbound.EnqueueResize(columns, rows);
        }
    }

    public void Dispose() => Close(null);

    private void ReadLoop()
    {
        var shell = _shell!;
        var buffer = new byte[64 * 1024];
        string? reason = null;
        try
        {
            while (true)
            {
                var read = shell.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        Close(reason);
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _closeReason = reason;
        _outbound.Dispose();

        var shell = _shell;
        var client = _client;
        var route = _route;
        _ = Task.Run(() =>
        {
            // Fechar o canal e desconectar esperam o servidor (até segundos se a rede caiu):
            // sempre em segundo plano, nunca na thread de interface.
            try
            {
                shell?.Dispose();
            }
            catch (Exception)
            {
                // Encerramento: falhas ao fechar o canal não mudam o resultado.
            }

            try
            {
                client?.Disconnect();
            }
            catch (Exception)
            {
            }
            finally
            {
                client?.Dispose();
                route?.Dispose();
                foreach (var owned in _owned)
                {
                    owned.Dispose();
                }
            }
        });

        Closed?.Invoke(reason);
    }
}
