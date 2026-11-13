using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.App.ViewModels;
using RootDock.Core;
using RootDock.Core.LocalShell;
using RootDock.Core.Sessions;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RootDock.App;

/// <summary>
/// Barra de menus na barra de título (como o VS Code e o Visual Studio): Arquivo, Editar, Exibir,
/// Sessão, Ferramentas e Ajuda. Cada menu é montado ao abrir, então o que não se aplica ao que
/// está ativo (terminal, Área de Trabalho Remota, sessão selecionada) aparece desabilitado.
/// </summary>
public partial class MainWindow
{
    private void BuildMainMenu()
    {
        MainMenu.Items.Clear();
        AddTopMenu("_Arquivo", FileMenu);
        AddTopMenu("_Editar", EditMenu);
        AddTopMenu("E_xibir", ViewMenu);
        AddTopMenu("_Sessão", SessionMenu);
        AddTopMenu("_Ferramentas", ToolsMenu);
        AddTopMenu("A_juda", HelpMenu);
    }

    private void AddTopMenu(string header, Func<IEnumerable<object>> build)
    {
        var top = new MenuItem { Header = header };
        top.Items.Add(new MenuItem { Header = "…" }); // marca que há submenu até montar
        top.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, top))
            {
                return; // submenu interno abrindo
            }

            top.Items.Clear();
            foreach (var item in build())
            {
                top.Items.Add(item);
            }
        };
        MainMenu.Items.Add(top);
    }

    // ------------------------------------------------------------------ menus

    private IEnumerable<object> FileMenu()
    {
        yield return Command("Nova sessão...", () => OnNewSession(this, new RoutedEventArgs()));
        yield return Command("Conexão rápida", FocusQuickConnect, "Ctrl+Shift+K");
        yield return Command("Novo terminal local", () => OpenLocalShell(_settings.DefaultLocalShell), "Ctrl+Shift+T");
        var shells = new MenuItem { Header = "Terminal local" };
        foreach (var shell in LocalShells.Available())
        {
            var kind = shell.Kind;
            shells.Items.Add(Command(shell.DisplayName + (kind == _settings.DefaultLocalShell ? "  (padrão)" : ""), () =>
            {
                _settings.DefaultLocalShell = kind;
                SaveSettings();
                OpenLocalShell(kind);
            }));
        }

        yield return shells;
        yield return new Separator();

        yield return Command("Arquivos: Computador ↔ Servidor", OpenActiveServerFiles, enabled: CanOpenActiveServerFiles);
        var save = new MenuItem { Header = "Salvar o conteúdo do terminal", IsEnabled = ActiveTerminal is not null };
        save.Items.Add(Command("Como texto (.txt)", () => PageCommand("export", "txt")));
        save.Items.Add(Command("Como HTML (com cores)", () => PageCommand("export", "html")));
        yield return save;
        yield return new Separator();

        yield return Command("Reabrir as abas da última vez", RestoreWorkspace, enabled: RestorableTabs().Count > 0);
        yield return new Separator();
        yield return Command("Sair", Close, "Alt+F4");
    }

    private IEnumerable<object> EditMenu()
    {
        var terminal = ActiveTerminal is not null;
        yield return Command("Copiar", () => PageCommand("copy"), "Ctrl+Shift+C", terminal);
        yield return Command("Colar", () => PageCommand("paste"), "Ctrl+V", terminal);
        yield return Command("Selecionar tudo", () => PageCommand("selectAll"), enabled: terminal);
        yield return new Separator();
        yield return Command("Buscar no terminal", () => PageCommand("search"), "Ctrl+Shift+F", terminal);
        yield return Command("Print do código (imagem)...", () => PageCommand("snap"), "Ctrl+Shift+S", _workspace.ActivePaneId is not null);
        yield return Command("Limpar a tela do terminal", () => PageCommand("clear"), enabled: terminal);
        yield return new Separator();
        yield return Command("Buscar sessões", FocusSessionSearch, "Ctrl+F");
        yield return Command("Histórico de comandos...", OpenHistory, "Ctrl+Shift+H");
    }

    private IEnumerable<object> ViewMenu()
    {
        yield return Toggle("Sessões", SessionsTab.IsChecked == true, () => SessionsTab.IsChecked = true);
        yield return Toggle("Arquivos", FilesTab.IsChecked == true, () => FilesTab.IsChecked = true);
        yield return Toggle("Biblioteca de comandos", IsCommandsPanelOpen, () => SetCommandsPanel(!IsCommandsPanelOpen), "Ctrl+Shift+L");
        yield return new Separator();

        var hasPane = _workspace.ActivePaneId is not null;
        yield return Command("Dividir à direita", () => PageCommand("splitV"), "Ctrl+Shift+D", hasPane);
        yield return Command("Dividir abaixo", () => PageCommand("splitH"), "Ctrl+Shift+E", hasPane);
        var arrange = new MenuItem { Header = "Organizar", IsEnabled = hasPane };
        arrange.Items.Add(Command("Em grade", () => PageCommand("arrange", "grid")));
        arrange.Items.Add(Command("Lado a lado (colunas)", () => PageCommand("arrange", "cols")));
        arrange.Items.Add(Command("Um embaixo do outro (linhas)", () => PageCommand("arrange", "rows")));
        arrange.Items.Add(new Separator());
        arrange.Items.Add(Command("Juntar todas as abas nesta", () => PageCommand("arrange", "gather")));
        arrange.Items.Add(Command("Separar em abas", () => PageCommand("arrange", "separate")));
        yield return arrange;
        yield return Command("Fechar painel", () => PageCommand("close"), "Ctrl+Shift+W", hasPane);
        yield return Command("Próxima aba", () => PageCommand("nextTab"), "Ctrl+Tab", hasPane);
        yield return Command("Aba anterior", () => PageCommand("prevTab"), "Ctrl+Shift+Tab", hasPane);
        yield return new Separator();

        yield return Command("Aumentar a fonte", () => PageCommand("fontUp"), "Ctrl+=");
        yield return Command("Diminuir a fonte", () => PageCommand("fontDown"), "Ctrl+-");
        yield return Command("Tamanho padrão da fonte", () => PageCommand("fontReset"), "Ctrl+0");
        yield return new Separator();

        var themes = new MenuItem { Header = "Tema" };
        foreach (var theme in Services.AppThemes.All)
        {
            var id = theme.Id;
            themes.Items.Add(Toggle(theme.Name, id == _settings.AppTheme, () => ApplyTheme(id, withTerminal: true)));
        }

        themes.Items.Add(new Separator());
        themes.Items.Add(Command("Galeria de temas...", OpenThemeGallery));
        yield return themes;
        yield return Command("Aparência do terminal...", OpenTerminalSettings);
    }

    private IEnumerable<object> SessionMenu()
    {
        var selected = (SessionTree.SelectedItem as SessionNode)?.Session;
        var name = selected is null ? "" : $" \"{Shorten(selected.Name)}\"";
        yield return Command($"Conectar{name}", () => Connect(selected!), "Enter", selected is not null);
        yield return Command("Editar sessão...", () => EditSession(selected!), "F2", selected is not null);
        yield return Command("Duplicar sessão", () => DuplicateSession(selected!), enabled: selected is not null);
        yield return Command("Logins salvos...", () => OpenLogins(selected!),
            enabled: selected is { Kind: SessionKind.Ssh or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Ftp });
        yield return Command("Túneis...", () => OpenTunnels(selected!), enabled: selected is { Kind: SessionKind.Ssh });
        yield return Command("Instalar minha chave SSH no servidor", () => _ = InstallKeyAsync([selected!]), enabled: selected is { Kind: SessionKind.Ssh });
        yield return new Separator();

        yield return Command("Executar em vários servidores...", () => OpenMultiExec(PickedEntries()));
        var terminal = ActiveTerminal is not null;
        yield return Command(ActiveTerminal?.Recorder is not null ? "Parar a gravação do terminal" : "Gravar o terminal", () => PageCommand("record"), enabled: terminal);
        yield return Command("Gravações...", () => RecordingsWindow.ShowFor(this, _workspace.RecordingsDirectory!, _workspace));
    }

    private IEnumerable<object> ToolsMenu()
    {
        var import = new MenuItem { Header = "Importar sessões" };
        import.Items.Add(Command("Do MobaXterm (MobaXterm.ini)...", () => _ = ImportMobaXtermAsync()));
        import.Items.Add(Command("Do OpenSSH (~/.ssh/config)...", () => _ = ImportSshConfigAsync()));
        yield return import;
        yield return new Separator();
        yield return Command("Chave SSH deste computador...", () => SshKeyWindow.ShowFor(this));
        yield return Command("Executar em vários servidores...", () => OpenMultiExec(PickedEntries()));
        yield return new Separator();
        yield return Command("Macros...", () => OpenMacros());
        yield return _recording is null
            ? Command("Gravar macro no terminal ativo", () => StartMacroRecording(), enabled: _macroRunner is null && _workspace.ActiveSession is { State: Terminal.TerminalState.Connected })
            : Command("Parar a gravação e salvar a macro", () => StopMacroRecording(save: true));
        if (_macroRunner is not null)
        {
            yield return Command("Parar a macro em execução", StopMacro);
        }

        yield return new Separator();
        yield return Command("Transferir entre servidores (Servidor ↔ Servidor)...", () => OpenServerTransfer(
            _workspace.ActiveSession is { IsSsh: true } active ? active.Entry : null,
            _workspace.ActiveSession is { IsSsh: true, Files: { } files } ? files.CurrentPath : null));
        yield return Command("Ferramentas de rede (ping, DNS, portas, SSL...)", () => OpenNetworkTools());
        yield return Command("Histórico de comandos...", OpenHistory, "Ctrl+Shift+H");
        yield return Command("Gravações de terminal...", () => RecordingsWindow.ShowFor(this, _workspace.RecordingsDirectory!, _workspace));
        yield return new Separator();
        yield return Command("Abrir a pasta de dados do RootDock", () => OpenFolder(_paths.DataDirectory));
    }

    private IEnumerable<object> HelpMenu()
    {
        yield return Command("Atalhos de teclado", () => ShortcutsWindow.ShowFor(this));
        yield return Command("Abrir a pasta de registros (logs)", () => OpenFolder(AppPaths.Default.LogsDirectory));
        yield return new Separator();
        if (Updates is { Stage: UpdateStage.Ready } ready)
        {
            yield return Command($"Reiniciar e atualizar para a versão {ready.AvailableVersion}", RestartForUpdate);
        }

        yield return Command("Verificar atualizações...", () => OpenUpdates(checkNow: true));
        yield return Command("Versões e novidades no GitHub", UpdateService.OpenReleasesPage);
        yield return new Separator();
        yield return Command("Sobre o RootDock", () => AboutWindow.ShowFor(this, _paths.DataDirectory, () => OpenUpdates(checkNow: true)));
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>Terminal ativo (não editor, RDP ou VNC), ou null.</summary>
    private Terminal.TerminalSession? ActiveTerminal =>
        _workspace.ActivePaneId is { } id ? _workspace.Sessions.FirstOrDefault(s => s.Id == id) : null;

    private bool CanOpenActiveServerFiles =>
        _workspace.ActiveSession is { IsSsh: true, Files: not null } || _rdp.Describe(_workspace.ActivePaneId) is not null;

    /// <summary>Transferência entre dois servidores; <paramref name="first"/> já entra na coluna da esquerda.</summary>
    private void OpenServerTransfer(Core.Sessions.SessionEntry? first, string? path) =>
        ServerTransferWindow.ShowFor(this, _sessions.Document.Sessions, _serverLinks, _transfers, first, path);

    /// <summary>Gerenciador em duas colunas do servidor ativo (SFTP do terminal SSH ou SMB da Área de Trabalho Remota).</summary>
    private void OpenActiveServerFiles()
    {
        if (_workspace.ActiveSession is { IsSsh: true, Files: { } files })
        {
            FilesView.OpenManager?.Invoke(files);
        }
        else if (_rdp.Describe(_workspace.ActivePaneId) is { } rdp)
        {
            _ = _rdp.OpenFilesAsync(rdp.Id);
        }
    }

    /// <summary>Ação executada pela tela dos terminais (copiar, dividir, fonte...).</summary>
    private void PageCommand(string action, string? arg = null)
    {
        _workspace.Send(new { type = "menu", action, arg });
        _workspace.FocusTerminal();
    }

    private void FocusSessionSearch()
    {
        SessionsTab.IsChecked = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string Shorten(string text) => text.Length > 28 ? text[..27] + "…" : text;

    private static MenuItem Command(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Toggle(string header, bool isChecked, Action action, string? gesture = null)
    {
        // Marcável só para mostrar o estado: o menu é montado de novo a cada abertura.
        var item = Command(header, action, gesture);
        item.IsCheckable = true;
        item.IsChecked = isChecked;
        return item;
    }
}
