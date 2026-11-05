using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Security;
using RootDock.Core.Ssh;

namespace RootDock.Core.MultiExec;

/// <summary>
/// Executa comandos por SSH (canal de execução, sem terminal). Usa a conexão de um terminal já
/// aberto quando houver, ou abre uma própria com as credenciais salvas, sem perguntar nada:
/// servidor com chave desconhecida ou sem senha salva falha com a explicação.
/// </summary>
public sealed class SshCommandExecutor : ICommandExecutor
{
    private readonly SshClient _client;
    private readonly bool _owned;
    private readonly List<IDisposable> _resources;

    private SshCommandExecutor(SshClient client, bool owned, List<IDisposable> resources)
    {
        _client = client;
        _owned = owned;
        _resources = resources;
    }

    /// <summary>Reaproveita a conexão de um terminal aberto (não fecha ao terminar).</summary>
    public static SshCommandExecutor Borrow(SshClient client) => new(client, owned: false, []);

    /// <summary>Conexão própria só para a execução.</summary>
    public static async Task<SshCommandExecutor> ConnectAsync(
        string host, int port, SshCredentials credentials, KnownHostsStore knownHosts, CancellationToken ct)
    {
        var owned = new List<IDisposable>();
        var info = SshClientFactory.CreateConnectionInfo(host, port, credentials, NonInteractive.Instance, owned);
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var rejected = false;
        SshClientFactory.AttachHostKeyVerification(client, host, port, knownHosts, NonInteractive.Instance, () => rejected = true);
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

            if (rejected)
            {
                throw new InvalidOperationException("Chave do servidor ainda não confirmada: conecte uma vez pelo terminal para conferir e salvar.", ex);
            }

            if (ex is SshPromptCanceledException prompt)
            {
                throw new InvalidOperationException($"O servidor pediu \"{prompt.Prompt}\" (ex.: código 2FA). Aqui não dá para responder: conecte pelo terminal ou use uma chave SSH.", ex);
            }

            if (ex is SshAuthenticationException)
            {
                throw new InvalidOperationException($"Login recusado para {credentials.Username}: salve a senha da sessão (botão direito → Logins salvos) ou use uma chave SSH.", ex);
            }

            throw;
        }

        return new SshCommandExecutor(client, owned: true, owned);
    }

    /// <summary>Saída guardada por servidor: começo e fim (o meio de um log enorme não cabe na tela mesmo).</summary>
    internal const int OutputHeadChars = 256 * 1024;
    internal const int OutputTailChars = 64 * 1024;

    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = _client.CreateCommand(command);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        // Lê enquanto o comando roda: o SSH.NET não segura o servidor, então esperar o fim e pegar
        // cmd.Result acumularia a saída inteira (journalctl de 1 GB x 20 servidores) na memória.
        var execute = cmd.ExecuteAsync(limit.Token);
        var output = Task.Run(() => BoundedText.ReadAsync(cmd.OutputStream, OutputHeadChars, OutputTailChars, limit.Token));
        var error = Task.Run(() => BoundedText.ReadAsync(cmd.ExtendedOutputStream, OutputHeadChars / 4, OutputTailChars / 4, limit.Token));
        try
        {
            await execute.ConfigureAwait(false);
            return new CommandResult(cmd.ExitStatus, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    public void Dispose()
    {
        if (!_owned)
        {
            return;
        }

        _client.Dispose();
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }
    }

    private sealed class NonInteractive : ISshInteraction
    {
        public static readonly NonInteractive Instance = new();

        public bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob) => false;

        public string? AnswerPrompt(string host, string instruction, string prompt, bool echo) => null;
    }
}
