using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using Renci.SshNet.Common;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.Core.LocalShell;
using RootDock.Core.Serial;
using RootDock.Core.Telnet;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Ssh;
using RootDock.Core.Terminal;

namespace RootDock.App.Terminal;

/// <summary>
/// Abre a conexão de uma sessão: resolve credenciais (cofre, chave, perguntas no terminal),
/// verifica a chave do servidor e repete a autenticação quando a senha está errada.
/// Executa na thread de interface; a parte de rede roda em segundo plano.
/// </summary>
public sealed class SessionConnector
{
    private const int MaxAuthAttempts = 3;
    private const string Dim = "\u001b[90m";
    private const string Red = "\u001b[31m";
    private const string Bold = "\u001b[1m";
    private const string Cyan = "\u001b[36m";
    private const string Reset = "\u001b[0m";

    private readonly Window _owner;
    private readonly KnownHostsStore _knownHosts;
    private readonly CredentialVault _vault;
    private readonly SessionManager _sessions;
    private readonly SessionLogins _logins;
    private readonly HashSet<Guid> _declined = new();

    public SessionConnector(Window owner, KnownHostsStore knownHosts, CredentialVault vault, SessionManager sessions)
    {
        _owner = owner;
        _knownHosts = knownHosts;
        _vault = vault;
        _sessions = sessions;
        _logins = new SessionLogins(vault);
    }

    public async Task<ITerminalConnection> ConnectAsync(
        SessionEntry entry, int columns, int rows, ITerminalPrompter terminal, CancellationToken ct,
        SshCredentials? reuse = null, IReadOnlyList<SshHop>? reuseJumps = null)
    {
        if (entry.Kind == SessionKind.LocalShell)
        {
            var shell = LocalShells.Resolve(entry.LocalShell ?? LocalShellKind.PowerShell)
                ?? throw new InvalidOperationException("Este shell não está instalado nesta máquina.");
            var local = new ConPtyConnection(shell.CommandLine);
            await local.ConnectAsync(columns, rows, ct);
            return local;
        }

        if (entry.Kind == SessionKind.Telnet)
        {
            return await ConnectTelnetAsync(entry, columns, rows, terminal, ct);
        }

        if (entry.Kind == SessionKind.Serial)
        {
            var settings = entry.Serial ?? throw new InvalidOperationException("Sessão serial sem porta configurada. Edite a sessão.");
            Status(terminal, $"Abrindo {settings.Describe()}...");
            var serial = new SerialConnection(settings);
            await Task.Run(() => serial.ConnectAsync(columns, rows, ct), ct);
            _sessions.Touch(entry);
            Status(terminal, "Porta aberta. Tecle Enter para o equipamento mostrar o prompt.");
            return serial;
        }

        return await ConnectSshAsync(entry, columns, rows, terminal, reuse, reuseJumps, ct);
    }

    // Telnet: o próprio equipamento pede login e senha no terminal. Com usuário na sessão (e a
    // senha em Logins salvos), o RootDock digita os dois sozinho quando o prompt aparecer.
    private async Task<ITerminalConnection> ConnectTelnetAsync(
        SessionEntry entry, int columns, int rows, ITerminalPrompter terminal, CancellationToken ct)
    {
        TelnetAutoLogin? login = null;
        if (entry.Username?.Trim() is { Length: > 0 } user)
        {
            var saved = _sessions.Contains(entry.Id);
            if (saved && TryVault(() => _logins.Migrate(entry), false))
            {
                _sessions.Save();
            }

            var password = saved ? TryVault(() => _logins.ReadPassword(entry, user), null) : null;
            login = new TelnetAutoLogin(user, password);
        }

        Status(terminal, $"Conectando a {entry.Host}:{entry.Port} (Telnet, sem criptografia)...");
        var telnet = new TelnetConnection(entry.Host, entry.Port, login);
        await telnet.ConnectAsync(columns, rows, ct);
        _sessions.Touch(entry);
        return telnet;
    }

    // Login no próprio terminal, como no PuTTY/MobaXterm:
    // - com logins salvos, pergunta qual usar (Enter = padrão, número, ou outro usuário);
    // - sem usuário, pergunta "Usuário:"; sem chave e sem senha no cofre, pergunta a senha;
    // - senha errada mostra "Acesso negado" e pergunta de novo;
    // - divisão de tela reaproveita o login do painel de origem.
    // A chave do servidor continua sendo confirmada numa janela.
    private async Task<ITerminalConnection> ConnectSshAsync(
        SessionEntry entry, int columns, int rows, ITerminalPrompter terminal, SshCredentials? reuse,
        IReadOnlyList<SshHop>? reuseJumps, CancellationToken ct)
    {
        var interaction = new TerminalSshInteraction(_owner, _knownHosts, terminal, ct);
        var saved = _sessions.Contains(entry.Id);
        if (saved && TryVault(() => _logins.Migrate(entry), false))
        {
            _sessions.Save();
        }

        string? username;
        string? password;
        string? keyPath;
        string? passphrase;
        var typedPassword = false;
        var typedPassphrase = false;
        var passwordFromVault = false;
        var useAgent = entry.UseAgent;
        List<ResolvedHop> jumps;

        if (reuse is not null)
        {
            username = reuse.Username;
            password = reuse.Password;
            keyPath = reuse.PrivateKeyPath;
            passphrase = reuse.KeyPassphrase;
            useAgent = reuse.UseAgent;
            jumps = (reuseJumps ?? []).Select(h => new ResolvedHop(h, null, false)).ToList();
        }
        else
        {
            // Bastions primeiro (o caminho até o servidor), depois o login do próprio servidor.
            jumps = await ResolveJumpsAsync(entry, terminal, ct);

            keyPath = entry.PrivateKeyPath;
            if (keyPath is not null && !File.Exists(keyPath))
            {
                Status(terminal, $"Chave privada não encontrada ({keyPath}); entrando com senha.");
                keyPath = null;
            }

            username = entry.Username?.Trim();
            var logins = saved ? TryVault<IReadOnlyList<SavedLogin>>(() => _logins.List(entry), []) : [];
            // Menu só com mais de um login salvo (igual ao RDP e ao FTP): com um só, não há o que
            // escolher e ele entra direto. Outro usuário: Logins salvos ou a conexão rápida (user@host).
            if (logins.Count > 1 && entry.ChooseLoginOnConnect)
            {
                username = await ChooseLoginAsync(terminal, entry, logins, keyPath is not null, ct);
            }
            else if (string.IsNullOrEmpty(username) && logins.Count > 0)
            {
                username = (logins.FirstOrDefault(l => l.IsDefault) ?? logins[0]).Username;
            }

            while (string.IsNullOrEmpty(username))
            {
                username = (await terminal.AskAsync("Usuário: ", echo: true, ct) ?? throw new OperationCanceledException()).Trim();
            }

            var user = username;
            password = saved ? TryVault(() => _logins.ReadPassword(entry, user), null) : null;
            passwordFromVault = password is not null;
            passphrase = keyPath is null ? null : TryVault(() => _vault.Read(CredentialVault.KeyPassphraseKey(entry.Id)), null);
        }

        var target = $"{username}@{entry.Host}";

        // O SSH.NET envia a senha na autenticação, então ela é pedida antes de conectar
        // (com o agente ligado, primeiro tenta as chaves dele; a senha só se forem recusadas).
        if (keyPath is null && password is null && !useAgent)
        {
            password = await AskPasswordAsync(terminal, target, ct);
            typedPassword = true;
        }

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Status(terminal, attempt == 1
                ? $"Conectando a {target}{(entry.Port == 22 ? "" : ":" + entry.Port)}{(reuse is not null ? " (mesmo login do painel ao lado)" : "")}..."
                : "Tentando novamente...");
            var connection = new SshTerminalConnection(
                entry.Host, entry.Port, new SshCredentials(username, password, keyPath, passphrase, useAgent), _knownHosts, interaction,
                jumps.Select(j => j.Hop).ToList());
            try
            {
                // Rede e troca de chaves fora da thread de interface: o diálogo da chave do servidor precisa dela livre.
                await Task.Run(() => connection.ConnectAsync(columns, rows, ct), ct);

                _sessions.Touch(entry);
                if (reuse is null)
                {
                    _ = OfferToRememberAsync(entry, terminal, username, typedPassword ? password : null, typedPassphrase ? passphrase : null);
                }

                return connection;
            }
            catch (PrivateKeyPassphraseRequiredException)
            {
                connection.Dispose();
                if (passphrase is not null)
                {
                    Error(terminal, "Senha da chave incorreta.");
                }

                var answer = await terminal.AskAsync($"Senha da chave {Path.GetFileName(keyPath)} (Enter vazio para usar a senha do usuário): ", echo: false, ct)
                    ?? throw new OperationCanceledException();
                if (answer.Length > 0)
                {
                    passphrase = answer;
                    typedPassphrase = true;
                    continue;
                }

                keyPath = null;
                passphrase = null;
                if (password is null)
                {
                    password = await AskPasswordAsync(terminal, target, ct);
                    typedPassword = true;
                }
            }
            catch (JumpHostException jump)
            {
                connection.Dispose();
                // Senha salva do bastion recusada: sai do cofre (a próxima tentativa pergunta).
                if (jump.InnerException is SshAuthenticationException &&
                    jumps.FirstOrDefault(j => j.Hop == jump.Hop) is { FromVault: true, Entry: { } bastion })
                {
                    var user = jump.Hop.Credentials.Username;
                    TryVault(() => { _logins.ForgetPassword(bastion, user); return true; }, false);
                }

                throw;
            }
            catch (SshAuthenticationException) when (attempt < MaxAuthAttempts)
            {
                connection.Dispose();
                if (passwordFromVault)
                {
                    // A senha salva não serve mais: tira do cofre para não repetir o erro (o usuário continua na lista).
                    var user = username;
                    TryVault(() => { _logins.ForgetPassword(entry, user); return true; }, false);
                    passwordFromVault = false;
                    Error(terminal, $"Acesso negado: a senha salva de {username} não funciona mais e foi apagada do cofre.");
                }
                else
                {
                    Error(terminal, keyPath is null ? "Acesso negado." : "Acesso negado: a chave foi recusada.");
                }

                password = await AskPasswordAsync(terminal, target, ct);
                typedPassword = true;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    private sealed record ResolvedHop(SshHop Hop, SessionEntry? Entry, bool FromVault);

    /// <summary>
    /// Bastions (ProxyJump) da sessão, do mais distante para o mais próximo do servidor, com as
    /// credenciais de cada um (cofre, chave, agente ou pergunta no terminal).
    /// </summary>
    private async Task<List<ResolvedHop>> ResolveJumpsAsync(SessionEntry entry, ITerminalPrompter terminal, CancellationToken ct)
    {
        var chain = new List<SessionEntry>();
        var seen = new HashSet<Guid> { entry.Id };
        for (var next = entry.JumpSessionId; next is Guid id;)
        {
            var bastion = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException("O bastion configurado nesta sessão não existe mais. Edite a sessão e escolha outro.");
            if (!seen.Add(bastion.Id) || chain.Count >= 5)
            {
                throw new InvalidOperationException("Os bastions (ProxyJump) formam um ciclo ou passam de 5 saltos. Revise as sessões.");
            }

            if (bastion.Kind != SessionKind.Ssh)
            {
                throw new InvalidOperationException($"\"{bastion.Name}\" não é uma sessão SSH e não pode ser bastion.");
            }

            chain.Insert(0, bastion);
            next = bastion.JumpSessionId;
        }

        var hops = new List<ResolvedHop>();
        foreach (var bastion in chain)
        {
            if (TryVault(() => _logins.Migrate(bastion), false))
            {
                _sessions.Save();
            }

            var user = bastion.Username?.Trim();
            while (string.IsNullOrEmpty(user))
            {
                user = (await terminal.AskAsync($"Usuário do bastion {bastion.Name}: ", echo: true, ct) ?? throw new OperationCanceledException()).Trim();
            }

            var key = bastion.PrivateKeyPath is { } path && File.Exists(path) ? path : null;
            var login = user;
            var password = TryVault(() => _logins.ReadPassword(bastion, login), null);
            var fromVault = password is not null;
            var passphrase = key is null ? null : TryVault(() => _vault.Read(CredentialVault.KeyPassphraseKey(bastion.Id)), null);
            if (key is null && password is null && !bastion.UseAgent)
            {
                password = await terminal.AskAsync($"Senha de {user}@{bastion.Host} (bastion {bastion.Name}): ", echo: false, ct)
                    ?? throw new OperationCanceledException();
            }

            hops.Add(new ResolvedHop(
                new SshHop(bastion.Name, bastion.Host, bastion.Port, new SshCredentials(user, password, key, passphrase, bastion.UseAgent)),
                bastion,
                fromVault));
        }

        if (hops.Count > 0)
        {
            Status(terminal, $"Caminho: {string.Join(" → ", chain.Select(c => c.Name))} → {entry.Name}");
        }

        return hops;
    }

    /// <summary>
    /// Lista os logins salvos no terminal e lê a escolha. Retorna o usuário escolhido, ou null
    /// para "Outro usuário" (aí o terminal pergunta "Usuário:").
    /// </summary>
    private static async Task<string?> ChooseLoginAsync(
        ITerminalPrompter terminal, SessionEntry entry, IReadOnlyList<SavedLogin> logins, bool hasKey, CancellationToken ct)
    {
        terminal.Write($"{Bold}Logins salvos para {entry.Name}{Reset}  {Dim}(Enter = {logins[0].Username}){Reset}\r\n");
        for (var i = 0; i < logins.Count; i++)
        {
            var login = logins[i];
            var notes = new List<string>();
            if (login.IsDefault)
            {
                notes.Add("padrão");
            }

            if (hasKey && login.IsDefault)
            {
                notes.Add("chave SSH");
            }
            else if (!login.HasPassword)
            {
                notes.Add("pede a senha");
            }

            var detail = notes.Count > 0 ? $"  {Dim}({string.Join(", ", notes)}){Reset}" : "";
            terminal.Write($"  {Cyan}{i + 1}{Reset}) {login.Username}{detail}\r\n");
        }

        var other = logins.Count + 1;
        terminal.Write($"  {Cyan}{other}{Reset}) Outro usuário\r\n");
        while (true)
        {
            var answer = (await terminal.AskAsync("Entrar como [1]: ", echo: true, ct) ?? throw new OperationCanceledException()).Trim();
            if (answer.Length == 0)
            {
                return logins[0].Username;
            }

            if (int.TryParse(answer, out var number))
            {
                if (number >= 1 && number <= logins.Count)
                {
                    return logins[number - 1].Username;
                }

                if (number == other)
                {
                    return null;
                }
            }
            else if (!answer.Contains(' '))
            {
                // Digitou o nome: um dos salvos ou um usuário novo direto.
                return logins.FirstOrDefault(l => string.Equals(l.Username, answer, StringComparison.Ordinal))?.Username ?? answer;
            }

            Error(terminal, $"Opção inválida: digite de 1 a {other}, o nome do usuário, ou Enter para {logins[0].Username}.");
        }
    }

    private static async Task<string> AskPasswordAsync(ITerminalPrompter terminal, string target, CancellationToken ct) =>
        await terminal.AskAsync($"Senha de {target}: ", echo: false, ct) ?? throw new OperationCanceledException();

    private static void Status(ITerminalPrompter terminal, string text) => terminal.Write($"{Dim}{text}{Reset}\r\n");

    private static void Error(ITerminalPrompter terminal, string text) => terminal.Write($"{Red}{text}{Reset}\r\n");

    // Depois de entrar com algo novo (usuário ou senha digitados), oferece guardar como login da
    // sessão. Só em sessões salvas e sem bloquear o terminal.
    private async Task OfferToRememberAsync(SessionEntry entry, ITerminalPrompter terminal, string username, string? password, string? passphrase)
    {
        if (!_sessions.Contains(entry.Id) || _declined.Contains(entry.Id))
        {
            return;
        }

        var newUser = !entry.Logins.Contains(username, StringComparer.Ordinal);
        if (!newUser && password is null && passphrase is null)
        {
            return;
        }

        var actions = new List<OfferAction>();
        string text;
        if (newUser && password is not null)
        {
            text = $"Salvar o login {username} nesta sessão?";
            actions.Add(new OfferAction("all", "Usuário e senha", Primary: true));
            actions.Add(new OfferAction("user", "Só o usuário"));
        }
        else if (newUser)
        {
            text = $"Salvar o usuário {username} nesta sessão?";
            actions.Add(new OfferAction("user", "Salvar", Primary: true));
        }
        else if (password is not null)
        {
            text = $"Salvar a senha de {username} no Cofre do Windows?";
            actions.Add(new OfferAction("all", "Salvar senha", Primary: true));
        }
        else
        {
            text = "Salvar a senha da chave SSH no Cofre do Windows?";
            actions.Add(new OfferAction("all", "Salvar", Primary: true));
        }

        actions.Add(new OfferAction("no", "Agora não"));
        var choice = await terminal.OfferAsync(text, actions);
        if (choice is null or "no")
        {
            // Não pergunta de novo nesta execução.
            _declined.Add(entry.Id);
            return;
        }

        var target = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == entry.Id) ?? entry;
        TryVault(() =>
        {
            if (choice == "user" || newUser || password is not null)
            {
                _logins.Save(target, username, choice == "all" ? password : null);
            }

            if (choice == "all" && passphrase is not null)
            {
                _vault.Save(CredentialVault.KeyPassphraseKey(entry.Id), username, passphrase);
            }

            return true;
        }, false);

        if (!ReferenceEquals(target, entry))
        {
            entry.Logins = [.. target.Logins];
            entry.Username = target.Username;
        }

        _sessions.Save();
    }

    private static T TryVault<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Win32Exception ex)
        {
            Log.Error("Cofre do Windows", ex);
            return fallback;
        }
    }

    /// <summary>Mensagem curta e acionável para exibir no terminal.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Conexão cancelada.",
        JumpHostException { InnerException: SshAuthenticationException } j =>
            $"Bastion \"{j.Hop.Name}\": usuário ou senha recusados. Pressione Enter para tentar de novo.",
        JumpHostException j => j.Message,
        SshAuthenticationException => "Autenticação recusada pelo servidor.",
        SshOperationTimeoutException => "Tempo esgotado: o servidor não respondeu.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Conexão recusada: a porta está fechada ou o SSH não está rodando.",
        SocketException { SocketErrorCode: SocketError.HostNotFound } => "Servidor não encontrado (nome não resolvido).",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "Tempo esgotado: o servidor não respondeu.",
        SocketException s => $"Falha de rede: {s.Message}",
        HostKeyRejectedException h => h.Message,
        SshConnectionException c => $"Conexão SSH falhou: {c.Message}",
        Win32Exception w => w.Message,
        _ => ex.Message,
    };
}

/// <summary>
/// Perguntas do SSH.NET (threads de fundo): a chave do servidor numa janela; perguntas extras
/// do servidor (código 2FA, troca de senha) no próprio terminal.
/// </summary>
internal sealed class TerminalSshInteraction : ISshInteraction
{
    private readonly Window _owner;
    private readonly KnownHostsStore _knownHosts;
    private readonly ITerminalPrompter _terminal;
    private readonly CancellationToken _ct;

    public TerminalSshInteraction(Window owner, KnownHostsStore knownHosts, ITerminalPrompter terminal, CancellationToken ct)
    {
        _owner = owner;
        _knownHosts = knownHosts;
        _terminal = terminal;
        _ct = ct;
    }

    public bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob)
    {
        var decision = _owner.Dispatcher.Invoke(() => HostKeyWindow.Ask(_owner, host, port, check));
        if (decision == HostKeyDecision.TrustAndSave)
        {
            _knownHosts.Trust(host, port, keyBlob, check.KeyType);
        }

        return decision != HostKeyDecision.Reject;
    }

    public string? AnswerPrompt(string host, string instruction, string prompt, bool echo)
    {
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            _terminal.Write(instruction.Trim().Replace("\n", "\r\n") + "\r\n");
        }

        var label = string.IsNullOrWhiteSpace(prompt) ? "Resposta: " : prompt.TrimEnd() + " ";
        // Chamado na thread do SSH.NET: pode esperar a resposta sem travar a interface.
        return _terminal.AskAsync(label, echo, _ct).GetAwaiter().GetResult();
    }
}
