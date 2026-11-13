using System.ComponentModel;
using System.IO;
using System.Windows;
using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;
using RootDock.Core.Ssh;

namespace RootDock.App.Remote;

/// <summary>
/// Um servidor ligado à janela de transferência entre servidores: os arquivos (SFTP) e, quando o
/// modo direto precisa, uma conexão para comandos. As duas são próprias da janela.
/// </summary>
public sealed class ServerLink : IDisposable
{
    private readonly Func<CancellationToken, Task<SshCommandClient>> _connectCommands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshCommandClient? _commands;
    private bool _disposed;

    internal ServerLink(SessionEntry entry, string user, RemoteFileSystem files, Func<CancellationToken, Task<SshCommandClient>> connectCommands)
    {
        Entry = entry;
        User = user;
        Files = files;
        _connectCommands = connectCommands;
    }

    public SessionEntry Entry { get; }

    public string User { get; }

    public RemoteFileSystem Files { get; }

    public bool IsProduction => Entry.Environment == SessionEnvironment.Production;

    /// <summary>Conexão para comandos (rsync, chave temporária), aberta na primeira vez que for pedida.</summary>
    public async Task<SshClient> GetCommandsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_commands is null || !_commands.Client.IsConnected)
            {
                _commands?.Dispose();
                _commands = await _connectCommands(ct);
            }

            return _commands.Client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Files.Dispose();
        _commands?.Dispose();
    }
}

/// <summary>
/// Liga uma sessão SSH à janela de transferência: usa o login do terminal aberto dela (inclusive
/// o caminho pelo bastion) ou, sem terminal, o login salvo. Não pergunta senha: o que faltar vira
/// uma mensagem dizendo o que fazer.
/// </summary>
public sealed class ServerLinkFactory(Window owner, TerminalWorkspace workspace, KnownHostsStore knownHosts, CredentialVault vault)
{
    private readonly SessionLogins _logins = new(vault);

    /// <summary>Sessões com terminal conectado agora.</summary>
    public HashSet<Guid> OpenSessions() =>
        workspace.Sessions.Where(s => s is { IsSsh: true, State: TerminalState.Connected, Credentials: not null }).Select(s => s.Entry.Id).ToHashSet();

    /// <summary>Linhas de known_hosts do servidor (para o outro servidor conferir a chave no modo direto).</summary>
    public IReadOnlyList<string> KnownHostLines(SessionEntry entry) => knownHosts.Lines(entry.Host, entry.Port);

    public async Task<ServerLink> ConnectAsync(SessionEntry entry, CancellationToken ct)
    {
        var interaction = new WindowSshInteraction(owner, knownHosts);
        var terminal = workspace.Sessions.FirstOrDefault(s =>
            s.Entry.Id == entry.Id && s is { IsSsh: true, State: TerminalState.Connected, Credentials: not null });
        SshCredentials credentials;
        string? connectHost = null;
        int? connectPort = null;
        if (terminal is not null)
        {
            credentials = terminal.Credentials!;
            connectHost = terminal.ConnectHost;
            connectPort = terminal.ConnectPort;
        }
        else
        {
            credentials = SavedCredentials(entry);
        }

        try
        {
            // Fora da thread de interface: a conferência da chave do servidor pode abrir um diálogo.
            var files = await Task.Run(() => RemoteFileSystem.ConnectAsync(
                entry.Host, entry.Port, credentials, knownHosts, interaction, ct, connectHost, connectPort), ct);
            return new ServerLink(entry, credentials.Username, files, token => Task.Run(() => SshCommandClient.ConnectAsync(
                entry.Host, entry.Port, credentials, knownHosts, interaction, token, connectHost, connectPort), token));
        }
        catch (SshAuthenticationException ex)
        {
            throw new InvalidOperationException(
                $"Login recusado para {credentials.Username}@{entry.Host}. Abra o terminal desta sessão (a janela usa o login dele) ou confira a senha em Logins salvos.", ex);
        }
        catch (SshPromptCanceledException ex)
        {
            throw new InvalidOperationException(
                $"O servidor pediu \"{ex.Prompt}\" (ex.: código 2FA). Abra o terminal desta sessão antes: a janela usa a conexão dele.", ex);
        }
    }

    private SshCredentials SavedCredentials(SessionEntry entry)
    {
        if (entry.JumpSessionId is not null)
        {
            throw new InvalidOperationException("Esta sessão passa por um bastion (ProxyJump): abra o terminal dela antes; a janela usa o caminho dele.");
        }

        string? user = entry.Username?.Trim();
        string? password = null;
        string? passphrase = null;
        var key = entry.PrivateKeyPath is { } path && File.Exists(path) ? path : null;
        try
        {
            if (string.IsNullOrEmpty(user))
            {
                var logins = _logins.List(entry);
                user = (logins.FirstOrDefault(l => l.IsDefault) ?? logins.FirstOrDefault())?.Username;
            }

            if (!string.IsNullOrEmpty(user))
            {
                password = _logins.ReadPassword(entry, user);
            }

            if (key is not null)
            {
                passphrase = vault.Read(CredentialVault.KeyPassphraseKey(entry.Id));
            }
        }
        catch (Win32Exception ex)
        {
            Log.Error("Transferência entre servidores: ler cofre", ex);
        }

        if (string.IsNullOrEmpty(user))
        {
            throw new InvalidOperationException("Esta sessão não tem usuário salvo: abra o terminal dela uma vez (ou salve o login em Logins salvos).");
        }

        if (password is null && key is null && !entry.UseAgent)
        {
            throw new InvalidOperationException("Esta sessão não tem senha salva nem chave SSH: abra o terminal dela (a janela usa o login dele) ou salve a senha em Logins salvos.");
        }

        return new SshCredentials(user, password, key, passphrase, entry.UseAgent);
    }

    /// <summary>Chave do servidor numa janela; perguntas extras (2FA) não têm onde ser respondidas aqui.</summary>
    private sealed class WindowSshInteraction(Window owner, KnownHostsStore knownHosts) : ISshInteraction
    {
        public bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob)
        {
            var decision = owner.Dispatcher.Invoke(() => HostKeyWindow.Ask(owner, host, port, check));
            if (decision == HostKeyDecision.TrustAndSave)
            {
                knownHosts.Trust(host, port, keyBlob, check.KeyType);
            }

            return decision != HostKeyDecision.Reject;
        }

        public string? AnswerPrompt(string host, string instruction, string prompt, bool echo) => null;
    }
}
