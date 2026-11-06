using System.Windows;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.App.ViewModels;
using RootDock.Core.Commands;
using RootDock.Core.Sessions;

namespace RootDock.App;

/// <summary>Biblioteca de comandos (painel direito): abrir/fechar, destino e envio seguro ao terminal.</summary>
public partial class MainWindow
{
    private const double CommandsMinWidth = 280;
    private const double CommandsMaxWidth = 640;

    /// <summary>Programas em que é seguro digitar um comando (prompt do shell ou tmux/screen).</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.Ordinal)
    {
        "bash", "sh", "dash", "zsh", "fish", "ksh", "mksh", "tcsh", "csh", "ash", "busybox", "login",
    };

    private CommandsViewModel? _commands;

    private bool IsCommandsPanelOpen => CommandsView.Visibility == Visibility.Visible;

    private void InitCommands()
    {
        CommandLibrary library;
        try
        {
            library = CommandLibrary.LoadBuiltIn();
        }
        catch (Exception ex)
        {
            Log.Error("Carregar biblioteca de comandos", ex);
            library = new CommandLibrary();
        }

        _commands = new CommandsViewModel(library, _settings, SaveSettings);
        _commands.FamilyChanged += () => UpdateCommandTarget(_workspace.ActiveSession);
        CommandsView.DataContext = _commands;
        _commands.RunRequested += (item, mode) => _ = RunCommandAsync(item, mode);
        CommandsView.CloseRequested += () => SetCommandsPanel(false);
        CommandsView.AddRequested += AddCustomCommand;
        CommandsView.EditRequested += EditCustomCommand;
        CommandsView.RemoveRequested += item => _ = RemoveCustomCommandAsync(item);
        CommandsView.Copied += text => Notify($"Copiado: {text}", false);
        _workspace.CommandsToggleRequested += () => SetCommandsPanel(!IsCommandsPanelOpen);
        UpdateCommandTarget(null);
    }

    private void SetCommandsPanel(bool open, bool focusSearch = true)
    {
        if (open)
        {
            _commands?.CollapseAll();
            CommandsColumn.MinWidth = CommandsMinWidth;
            CommandsColumn.Width = new GridLength(Math.Clamp(_settings.CommandsPanelWidth, CommandsMinWidth, CommandsMaxWidth));
            CommandsView.Visibility = Visibility.Visible;
            CommandsSplitter.Visibility = Visibility.Visible;
            if (focusSearch)
            {
                CommandsView.FocusSearch();
            }
        }
        else
        {
            RememberCommandsWidth();
            CommandsColumn.MinWidth = 0;
            CommandsColumn.Width = new GridLength(0);
            CommandsView.Visibility = Visibility.Collapsed;
            CommandsSplitter.Visibility = Visibility.Collapsed;
            _workspace.FocusTerminal();
        }

        _settings.CommandsPanelOpen = open;
        SaveSettings();
        _workspace.SetCommandsPanelState(open);
    }

    private void RememberCommandsWidth()
    {
        if (IsCommandsPanelOpen && CommandsColumn.ActualWidth >= CommandsMinWidth)
        {
            _settings.CommandsPanelWidth = CommandsColumn.ActualWidth;
        }
    }

    private Remote.ServerStatusViewModel? _watchedStatus;

    /// <summary>
    /// Mostra no painel para qual terminal os comandos vão (e por que não podem ir) e, no modo
    /// automático, troca a lista para o sistema dele (Ubuntu, RHEL, SUSE, Windows).
    /// </summary>
    private void UpdateCommandTarget(TerminalSession? session)
    {
        if (_commands is null)
        {
            return;
        }

        // O sistema de um servidor SSH só é conhecido depois da primeira leitura do monitor.
        if (!ReferenceEquals(_watchedStatus, session?.ServerStatus))
        {
            if (_watchedStatus is not null)
            {
                _watchedStatus.PropertyChanged -= OnWatchedStatusChanged;
            }

            _watchedStatus = session?.ServerStatus;
            if (_watchedStatus is not null)
            {
                _watchedStatus.PropertyChanged += OnWatchedStatusChanged;
            }
        }

        var (family, label) = DetectCommandFamily(session);
        _commands.SetDetected(family, label); // pode trocar a família e chamar de novo (FamilyChanged)

        var windowsList = _commands.Family == OsFamily.Windows;
        if (session is null)
        {
            _commands.SetTarget(null, windowsList ? "Os atalhos Win+R abrem neste computador; os comandos precisam de um terminal do Windows." : null);
            return;
        }

        string? warning = session.State switch
        {
            TerminalState.Closed => "Terminal desconectado. Pressione Enter nele para reconectar.",
            TerminalState.Connecting => "Conectando...",
            _ when IsWindowsShell(session) && !windowsList => "O terminal ativo é do Windows e a lista é de Linux. Escolha Windows em Sistema (ou use uma sessão SSH / WSL).",
            _ when !windowsList || family is null || family == OsFamily.Windows => null,
            _ => "A lista é de Windows e o terminal ativo é Linux. Os atalhos Win+R abrem neste computador.",
        };
        _commands.SetTarget(session.Entry.Name, warning);
    }

    private void OnWatchedStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Remote.ServerStatusViewModel.OsFamily))
        {
            Dispatcher.BeginInvoke(() => UpdateCommandTarget(_workspace.ActiveSession));
        }
    }

    /// <summary>Sistema do que está em foco: local (PowerShell/CMD/WSL), servidor SSH (pelo /etc/os-release) ou RDP.</summary>
    private (string? Family, string? Label) DetectCommandFamily(TerminalSession? session)
    {
        if (session is null)
        {
            return _rdp.Describe(_workspace.ActivePaneId) is { } rdp ? (OsFamily.Windows, $"Windows ({rdp.Name})") : (null, null);
        }

        if (session.Entry.Kind == SessionKind.LocalShell)
        {
            return IsWindowsShell(session) ? (OsFamily.Windows, "Windows (este PC)")
                : session.Entry.LocalShell == LocalShellKind.Wsl ? (OsFamily.Debian, "WSL")
                : (null, null);
        }

        return session.ServerStatus is { OsFamily: { } family } status ? (family, status.OsName) : (null, null);
    }

    /// <summary>Win+R: abre no computador; numa Área de Trabalho Remota, copia para colar lá.</summary>
    private void RunWindowsShortcut(CommandItemViewModel item)
    {
        var text = item.Definition.Cmd;
        if (_workspace.ActiveSession is null && _rdp.Describe(_workspace.ActivePaneId) is { } rdp)
        {
            SafeClipboard.SetText(text);
            Notify($"\"{text}\" copiado. Em {rdp.Name}: Win+R, Ctrl+V e Enter.", false);
            _commands?.RecordUse(item);
            return;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(text);
            System.Diagnostics.ProcessStartInfo start;
            if (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || System.IO.Directory.Exists(expanded))
            {
                start = new System.Diagnostics.ProcessStartInfo("explorer.exe", expanded);
            }
            else
            {
                var space = expanded.IndexOf(' ');
                start = space > 0
                    ? new System.Diagnostics.ProcessStartInfo(expanded[..space], expanded[(space + 1)..])
                    : new System.Diagnostics.ProcessStartInfo(expanded);
            }

            start.UseShellExecute = true;
            System.Diagnostics.Process.Start(start);
            _commands?.RecordUse(item);
            Notify($"Abrindo {item.Title} ({text}) neste computador.", false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            Notify($"Não foi possível abrir {text}: {ex.Message}", true);
        }
    }

    private static bool IsPowerShellSession(TerminalSession session) =>
        session.Entry.Kind == SessionKind.LocalShell && session.Entry.LocalShell is LocalShellKind.PowerShell or LocalShellKind.Pwsh;

    private async Task RunCommandAsync(CommandItemViewModel item, CommandSendMode mode)
    {
        if (item.IsMacro)
        {
            if (_settings.Macros.FirstOrDefault(m => m.Id == item.Id) is { } macro && await RunMacroAsync(macro))
            {
                _commands?.RecordUse(item);
            }

            return;
        }

        if (item.IsRun)
        {
            RunWindowsShortcut(item);
            return;
        }

        var session = _workspace.ActiveSession;
        if (session is null || session.State != TerminalState.Connected)
        {
            Notify(item.IsWindows ? "Abra um terminal do Windows (PowerShell, CMD ou SSH num servidor Windows) para enviar o comando." : "Abra ou conecte um terminal para enviar o comando.", true);
            return;
        }

        if (!item.IsWindows && item.Family is not null && IsWindowsShell(session))
        {
            Notify("O terminal ativo é do Windows (PowerShell/CMD) e este comando é de Linux. Escolha Windows em Sistema, no painel.", true);
            return;
        }

        if (item.IsWindows && DetectCommandFamily(session).Family is { } family && family != OsFamily.Windows)
        {
            Notify($"Este comando é do Windows e o terminal ativo é Linux ({session.Entry.Name}).", true);
            return;
        }

        var text = item.Render();
        if (text is null)
        {
            // Falta preencher algo: o formulário mostra o erro.
            item.IsFormOpen = true;
            return;
        }

        // Comando só do PowerShell num CMD (ou SSH num Windows, que costuma abrir no CMD): vai dentro do powershell.
        if (item.IsPowerShell && !IsPowerShellSession(session))
        {
            text = $"powershell -NoProfile -Command \"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        // Um editor ou monitor (vim, top, less...) aberto receberia o comando como teclas.
        if (!item.IsKeys && session.ForegroundProcess is { Length: > 0 } foreground && !IsShell(foreground) &&
            !await Prompts.ConfirmAsync(
                "Há um programa aberto no terminal",
                $"O terminal de {session.Entry.Name} está com \"{foreground}\" aberto. O comando seria digitado dentro dele.\n\n" +
                "Feche o programa antes (normalmente q, Ctrl+C ou :q no vim) ou envie mesmo assim.",
                "Enviar mesmo assim"))
        {
            return;
        }

        if (mode == CommandSendMode.Run && (item.IsCaution || CommandSafety.LooksDestructive(text)))
        {
            var production = session.Entry.Environment == SessionEnvironment.Production;
            var message = $"{item.Title}\n\n    {text}\n\n{item.Description}\n\nServidor: {session.Entry.Name}" +
                          (production ? "\n\nATENÇÃO: este é um servidor de PRODUÇÃO." : "");
            if (!await Prompts.ConfirmAsync(production ? "Executar em PRODUÇÃO?" : "Confirmar comando", message, "Executar", danger: true))
            {
                return;
            }
        }

        if (item.IsKeys)
        {
            session.Type(text);
        }
        else if (mode == CommandSendMode.Paste)
        {
            _workspace.PasteInto(session, text);
        }
        else
        {
            // Ctrl+U limpa o que estiver digitado pela metade no prompt antes do comando (só em
            // shells Unix: o CMD do Windows o escreveria como "^U").
            // Passa pelo xterm para a linha do comando ficar destacada como as digitadas.
            // Várias linhas: cada linha é digitada e executada em sequência.
            var clear = item.IsWindows || IsWindowsShell(session) ? "" : "\u0015";
            session.Type(clear + text.Replace("\r\n", "\n").Replace('\n', '\r') + "\r");
        }

        _workspace.Focus(session);
        item.IsFormOpen = false;
        _commands?.RecordUse(item);
        Notify(
            item.IsKeys ? $"Atalho {item.DisplayCommand} enviado para {session.Entry.Name}"
            : mode == CommandSendMode.Paste ? $"Colocado no terminal (revise e aperte Enter): {text}"
            : $"▶ {text}   →   {session.Entry.Name}",
            false);
    }

    private void AddCustomCommand()
    {
        if (CustomCommandWindow.Edit(this, null) is { } command)
        {
            _commands?.AddCustom(command);
            Notify($"\"{command.Title}\" adicionado em Meus comandos.", false);
        }
    }

    private void EditCustomCommand(CommandItemViewModel item)
    {
        if (item.IsMacro)
        {
            OpenMacros(item.Id);
            return;
        }

        if (_commands?.FindCustom(item.Id) is { } existing && CustomCommandWindow.Edit(this, existing) is { } edited)
        {
            _commands.UpdateCustom(edited);
        }
    }

    private async Task RemoveCustomCommandAsync(CommandItemViewModel item)
    {
        if (item.IsMacro)
        {
            if (await Prompts.ConfirmAsync("Excluir macro", $"Excluir a macro \"{item.Title}\"? Não dá para desfazer.", "Excluir", danger: true))
            {
                _settings.Macros.RemoveAll(m => m.Id == item.Id);
                SaveSettings();
                _commands?.MacrosChanged();
                MacrosWindow.RefreshIfOpen();
            }

            return;
        }

        if (await Prompts.ConfirmAsync("Excluir comando", $"Excluir \"{item.Title}\" de Meus comandos?", "Excluir", danger: true))
        {
            _commands?.RemoveCustom(item.Id);
        }
    }

    private static bool IsWindowsShell(TerminalSession session) =>
        session.Entry.Kind == SessionKind.LocalShell &&
        session.Entry.LocalShell is LocalShellKind.PowerShell or LocalShellKind.Pwsh or LocalShellKind.Cmd;

    private static bool IsShell(string name) =>
        ShellNames.Contains(name) ||
        name.StartsWith("tmux", StringComparison.Ordinal) ||
        name.StartsWith("screen", StringComparison.Ordinal);
}
