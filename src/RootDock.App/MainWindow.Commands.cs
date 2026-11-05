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

    /// <summary>Mostra no painel para qual terminal os comandos vão (e por que não podem ir).</summary>
    private void UpdateCommandTarget(TerminalSession? session)
    {
        if (_commands is null)
        {
            return;
        }

        if (session is null)
        {
            _commands.SetTarget(null, null);
            return;
        }

        string? warning = session.State switch
        {
            TerminalState.Closed => "Terminal desconectado. Pressione Enter nele para reconectar.",
            TerminalState.Connecting => "Conectando...",
            _ when IsWindowsShell(session) => "O terminal ativo é do Windows. Estes comandos são de Linux: use uma sessão SSH ou um terminal WSL.",
            _ => null,
        };
        _commands.SetTarget(session.Entry.Name, warning);
    }

    private async Task RunCommandAsync(CommandItemViewModel item, CommandSendMode mode)
    {
        var session = _workspace.ActiveSession;
        if (session is null || session.State != TerminalState.Connected)
        {
            Notify("Abra ou conecte um terminal para enviar o comando.", true);
            return;
        }

        if (IsWindowsShell(session))
        {
            Notify("O terminal ativo é do Windows (PowerShell/CMD). Estes comandos são de Linux.", true);
            return;
        }

        var text = item.Render();
        if (text is null)
        {
            // Falta preencher algo: o formulário mostra o erro.
            item.IsFormOpen = true;
            return;
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
            // Ctrl+U limpa o que estiver digitado pela metade no prompt antes do comando.
            // Passa pelo xterm para a linha do comando ficar destacada como as digitadas.
            // Macro (várias linhas): cada linha é digitada e executada em sequência.
            session.Type("\u0015" + text.Replace("\r\n", "\n").Replace('\n', '\r') + "\r");
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
        if (_commands?.FindCustom(item.Id) is { } existing && CustomCommandWindow.Edit(this, existing) is { } edited)
        {
            _commands.UpdateCustom(edited);
        }
    }

    private async Task RemoveCustomCommandAsync(CommandItemViewModel item)
    {
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
