using System.IO;
using System.Windows;
using Renci.SshNet.Common;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Security;
using RootDock.Core.Sessions;

namespace RootDock.App;

/// <summary>
/// "Minha chave SSH" e "Instalar minha chave SSH" (o ssh-copy-id do RootDock): usa a chave
/// deste computador (ou cria uma), adiciona ao authorized_keys do servidor pela conexão do
/// terminal, testa o login só com a chave e, se funcionar, passa a sessão a usá-la.
/// </summary>
public partial class MainWindow
{
    private void OnSshKey(object sender, RoutedEventArgs e) => SshKeyWindow.ShowFor(this);

    private void OnMultiExec(object sender, RoutedEventArgs e) => OpenMultiExec(PickedEntries());

    /// <summary>Menu "Ferramentas" da barra.</summary>
    private void OnToolsMenu(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = ToolsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in ToolsMenuItems())
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private IEnumerable<object> ToolsMenuItems()
    {
        yield return MenuItem("Ferramentas de rede (ping, DNS, portas, SSL...)", () => OpenNetworkTools());
        yield return MenuItem("Histórico de comandos...", OpenHistory, "Ctrl+Shift+H");
        yield return MenuItem("Gravações de terminal...", () => RecordingsWindow.ShowFor(this, _workspace.RecordingsDirectory!, _workspace));
        yield return MenuItem("Aparência do terminal...", OpenTerminalSettings);
    }

    private void OpenNetworkTools(string? tool = null, string? host = null) =>
        NetworkToolsWindow.ShowFor(this, ActiveHost, tool, host);

    /// <summary>Servidor do terminal ativo (para preencher as ferramentas de rede).</summary>
    private string? ActiveHost() =>
        _workspace.ActiveSession?.Entry is { Kind: SessionKind.Ssh or SessionKind.Telnet, Host.Length: > 0 } entry ? entry.Host : null;

    private void OpenHistory() =>
        HistoryWindow.ShowFor(this, _history, _workspace, command => OpenMultiExec(PickedEntries(), command));

    private void OpenTerminalSettings()
    {
        if (TerminalSettingsWindow.Edit(this, _settings.Terminal, _settings.FontSize) is not { } result)
        {
            _workspace.FocusTerminal();
            return;
        }

        _settings.Terminal = result.Appearance;
        _settings.FontSize = result.FontSize;
        SaveSettings();
        _workspace.Send(TerminalThemes.Message(_settings.Terminal, _settings.FontSize));
        _workspace.FocusTerminal();
    }

    /// <summary>Executar o mesmo comando em vários servidores (já marcando os escolhidos).</summary>
    private void OpenMultiExec(IEnumerable<SessionEntry> preselected, string? command = null) =>
        MultiExecWindow.Show(this, _sessions.Document.Sessions, preselected.Select(s => s.Id), _workspace, _knownHosts, _vault, _settings, SaveSettings, _paths.DataDirectory, command);

    private async Task InstallKeyAsync(IReadOnlyList<SessionEntry> entries)
    {
        var targets = entries.Where(e => e.Kind == SessionKind.Ssh).ToList();
        if (targets.Count == 0 || !EnsureWritable() || await EnsureKeyAsync() is not { } key)
        {
            return;
        }

        var production = targets.Any(e => e.Environment == SessionEnvironment.Production);
        var where = targets.Count == 1
            ? $"no servidor de \"{targets[0].Name}\""
            : $"nos {targets.Count} servidores selecionados";
        var text =
            $"Sua chave pública {key.DisplayType} ({key.Fingerprint}) será adicionada ao arquivo ~/.ssh/authorized_keys do usuário {where}. " +
            "Nenhuma linha existente é alterada ou removida.\n\n" +
            "Se um terminal ainda não estiver aberto, ele abre e pede o login (com senha) uma última vez. " +
            "Depois, a sessão passa a entrar com a chave, sem senha." +
            (production ? "\n\nATENÇÃO: inclui servidor de PRODUÇÃO." : "");
        if (!await Prompts.ConfirmAsync(production ? "Instalar sua chave em PRODUÇÃO?" : "Instalar sua chave SSH?", text, "Instalar", danger: production))
        {
            return;
        }

        var report = new List<string>();
        foreach (var entry in targets)
        {
            report.Add(await InstallKeyOnAsync(entry, key));
        }

        await Prompts.InfoAsync(
            targets.Count == 1 ? "Chave SSH" : "Chave SSH nos servidores",
            string.Join("\n\n", report));
    }

    /// <summary>Chave deste computador; se não houver, oferece criar.</summary>
    private async Task<SshKeyInfo?> EnsureKeyAsync()
    {
        if (SshKeys.FindDefault() is { } existing)
        {
            return existing;
        }

        var path = Path.Combine(SshKeys.DefaultDirectory, "id_ed25519");
        if (!await Prompts.ConfirmAsync(
                "Criar sua chave SSH?",
                $"Você ainda não tem uma chave SSH neste computador. O RootDock cria uma agora (Ed25519) em {path} e em seguida instala no servidor.",
                "Criar chave"))
        {
            return null;
        }

        try
        {
            return SshKeys.GenerateEd25519(SshKeys.DefaultComment());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Criar chave SSH", ex);
            await Prompts.InfoAsync("Não foi possível criar a chave", ex.Message);
            return null;
        }
    }

    /// <summary>Instala numa sessão e devolve uma linha de resultado para o resumo.</summary>
    private async Task<string> InstallKeyOnAsync(SessionEntry entry, SshKeyInfo key)
    {
        var name = $"\"{entry.Name}\"";
        var session = FindConnected(entry) ?? await OpenAndWaitAsync(entry);
        if (session?.SshClient is not { IsConnected: true } client || session.SshUsername is not { } user)
        {
            return $"✗ {name}: o login não foi concluído; nada foi alterado.";
        }

        var target = $"{user}@{entry.Host}";
        Notify($"Instalando sua chave em {target}...", false);
        string output;
        try
        {
            output = await Task.Run(() =>
            {
                using var command = client.CreateCommand(SshKeys.InstallCommand(key.PublicKey));
                command.CommandTimeout = TimeSpan.FromSeconds(30);
                var stdout = command.Execute();
                return stdout + command.Error;
            });
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or IOException)
        {
            Log.Error($"Instalar chave em {target}", ex);
            return $"✗ {name}: falha ao executar no servidor ({ex.Message}).";
        }

        var added = output.Contains(SshKeys.AddedMarker, StringComparison.Ordinal);
        if (!added && !output.Contains(SshKeys.ExistsMarker, StringComparison.Ordinal))
        {
            var answer = output.Trim();
            return $"✗ {name}: o servidor não aceitou o comando" +
                   (answer.Length > 0 ? $": {(answer.Length > 200 ? answer[..200] + "…" : answer)}" : ".") +
                   " (É preciso um servidor Linux/Unix com sh.)";
        }

        KeyLoginResult result;
        try
        {
            result = await SshKeys.TestLoginAsync(
                entry.Host, entry.Port, user, key.PrivateKeyPath, _knownHosts, CancellationToken.None, session.ConnectHost, session.ConnectPort);
        }
        catch (Exception ex)
        {
            Log.Error($"Testar login por chave em {target}", ex);
            return $"✗ {name}: chave {(added ? "instalada" : "já estava instalada")}, mas o teste de login falhou ({ex.Message}). A sessão continua com senha.";
        }

        var installed = added ? "instalada" : "já estava instalada";
        switch (result)
        {
            case KeyLoginResult.Accepted:
                UseKey(entry, key, user);
                return $"✓ {name}: chave {installed} em {target} e o login sem senha funcionou. A sessão agora usa a chave.";
            case KeyLoginResult.NeedsPassphrase:
                UseKey(entry, key, user);
                return $"✓ {name}: chave {installed} em {target}. Ela tem senha própria: o RootDock pede essa senha ao conectar e pode guardá-la no cofre.";
            default:
                return $"✗ {name}: chave {installed} em {target}, mas o servidor não aceitou o login por chave. " +
                       "Causas comuns: PubkeyAuthentication desligado no sshd_config, PermitRootLogin que só aceita senha, " +
                       "ou a pasta pessoal do usuário com permissão de escrita para outros. A sessão continua com senha.";
        }
    }

    /// <summary>Gerenciar os logins (usuário + senha) da sessão salva.</summary>
    private void OpenLogins(SessionEntry entry)
    {
        if (!EnsureWritable())
        {
            return;
        }

        var saved = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == entry.Id) ?? entry;
        LoginsWindow.ShowFor(this, saved, _sessions, _logins);
        RebuildTree();
    }

    /// <summary>Túneis da sessão salva (a instância do documento, para gravar direto).</summary>
    private void OpenTunnels(SessionEntry entry)
    {
        if (!EnsureWritable())
        {
            return;
        }

        var saved = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == entry.Id);
        if (saved is null)
        {
            Notify("Túneis ficam salvos na sessão: salve esta conexão como sessão para usá-los.", true);
            return;
        }

        TunnelsWindow.ShowFor(this, saved, _sessions, _tunnels);
        RebuildTree();
    }

    private void OnTunnelsBadge(object sender, RoutedEventArgs e)
    {
        if (_workspace.ActiveSession is { } session)
        {
            OpenTunnels(session.Entry);
        }
    }

    private void UpdateTunnelsBadge(TerminalSession? session)
    {
        var count = session is null ? 0 : _tunnels.RunningCount(session.Entry.Id);
        TunnelsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TunnelsText.Text = count == 1 ? "1 túnel" : $"{count} túneis";
    }

    private TerminalSession? FindConnected(SessionEntry entry) =>
        _workspace.Sessions.FirstOrDefault(s =>
            s.Entry.Id == entry.Id && s.State == TerminalState.Connected && s.SshClient is { IsConnected: true });

    /// <summary>Abre o terminal da sessão e espera o login (até 5 minutos).</summary>
    private async Task<TerminalSession?> OpenAndWaitAsync(SessionEntry entry)
    {
        var session = _workspace.Open(entry);
        Notify($"Entre em \"{entry.Name}\" no terminal; a chave é instalada assim que o login terminar.", false);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline && !session.IsDisposed && session.State != TerminalState.Closed)
        {
            if (session.State == TerminalState.Connected && session.SshClient is { IsConnected: true })
            {
                return session;
            }

            await Task.Delay(300);
        }

        return null;
    }

    /// <summary>Passa a sessão salva a entrar com a chave (e guarda o usuário que funcionou).</summary>
    private void UseKey(SessionEntry entry, SshKeyInfo key, string user)
    {
        foreach (var target in _sessions.Document.Sessions.Where(s => s.Id == entry.Id).Append(entry).Distinct())
        {
            target.PrivateKeyPath = key.PrivateKeyPath;
            if (string.IsNullOrWhiteSpace(target.Username))
            {
                target.Username = user;
            }
        }

        _sessions.Save();
        RebuildTree();
    }
}
