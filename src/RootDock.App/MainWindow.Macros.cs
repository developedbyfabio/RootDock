using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RootDock.App.Dialogs;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Commands;
using RootDock.Core.Macros;
using RootDock.Core.Sessions;

namespace RootDock.App;

/// <summary>
/// Macros: executar no terminal ativo (passo a passo, com o andamento no topo da janela), gravar o
/// que é digitado num terminal e a janela Ferramentas → Macros.
/// </summary>
public partial class MainWindow
{
    private MacroRunner? _macroRunner;
    private CancellationTokenSource? _macroCts;
    private CommandMacro? _runningMacro;
    private MacroProgress? _macroProgress;
    private MacroRecording? _recording;

    private sealed class MacroRecording(TerminalSession session)
    {
        public TerminalSession Session { get; } = session;

        public List<MacroStep> Steps { get; } = [];
    }

    /// <summary>Sessão aberta no RootDock vista pela macro.</summary>
    private sealed class SessionMacroTerminal(TerminalSession session) : IMacroTerminal
    {
        public bool IsConnected => !session.IsDisposed && session.State == TerminalState.Connected;

        public void Type(string text) => session.Type(text);
    }

    private void OpenMacros(string? select = null, bool rename = false) =>
        MacrosWindow.ShowFor(this, _settings, SaveSettings, () => _commands?.MacrosChanged(), RunMacroAsync, StartMacroRecording,
            () => _workspace.ActiveSession is { State: TerminalState.Connected } s ? s.Entry.Name : null, select, rename);

    // ------------------------------------------------------------------ executar

    /// <summary>Executa no terminal ativo. Retorna se começou (pode ter sido cancelada nas confirmações).</summary>
    private async Task<bool> RunMacroAsync(CommandMacro macro)
    {
        if (_macroRunner is not null)
        {
            Notify($"A macro \"{_runningMacro?.Name}\" ainda está rodando. Pare ela no topo da janela antes de começar outra.", true);
            return false;
        }

        if (_recording is not null)
        {
            Notify("Pare a gravação da macro antes de executar uma.", true);
            return false;
        }

        var session = _workspace.ActiveSession;
        if (session is null || session.State != TerminalState.Connected)
        {
            Notify("Abra ou conecte um terminal para executar a macro.", true);
            return false;
        }

        var steps = MacroRunner.Runnable(macro);
        if (steps.Count == 0)
        {
            Notify("A macro não tem passos para executar.", true);
            return false;
        }

        if (session.ForegroundProcess is { Length: > 0 } foreground && !IsShell(foreground) &&
            !await Prompts.ConfirmAsync("Há um programa aberto no terminal",
                $"O terminal de {session.Entry.Name} está com \"{foreground}\" aberto. Os comandos da macro seriam digitados dentro dele.\n\n" +
                "Feche o programa antes (normalmente q, Ctrl+C ou :q no vim) ou execute mesmo assim.", "Executar mesmo assim"))
        {
            return false;
        }

        var production = session.Entry.Environment == SessionEnvironment.Production;
        if (macro.LooksDestructive || production)
        {
            var commands = macro.Commands.ToList();
            var list = string.Join("\n", commands.Take(10).Select(c => (CommandSafety.LooksDestructive(c) ? "⚠ " : "    ") + c)) +
                       (commands.Count > 10 ? $"\n    ... e mais {commands.Count - 10}" : "");
            var title = production ? "Executar a macro em PRODUÇÃO?" : "Confirmar macro";
            var text = $"\"{DisplayName(macro)}\" vai executar em {session.Entry.Name}:\n\n{list}" +
                       (production ? "\n\nATENÇÃO: este é um servidor de PRODUÇÃO." : "");
            if (!await Prompts.ConfirmAsync(title, text, "Executar", danger: true))
            {
                return false;
            }
        }

        _ = ExecuteMacroAsync(macro, session, steps.Count);
        return true;
    }

    private async Task ExecuteMacroAsync(CommandMacro macro, TerminalSession session, int total)
    {
        var watcher = new TerminalOutputWatcher();
        // Sem Ctrl+U antes dos comandos (o painel usa para limpar a linha): o cmd de um servidor Windows
        // o escreve como "^U" e quebra o comando; a macro já espera o prompt limpo entre os passos.
        var runner = new MacroRunner(macro, new SessionMacroTerminal(session), watcher);
        runner.Progress += p =>
        {
            _macroProgress = p;
            RefreshMacroPill();
        };
        _macroRunner = runner;
        _runningMacro = macro;
        _macroProgress = null;
        _macroCts = new CancellationTokenSource();
        session.OutputObserved = watcher.Append;
        _workspace.Focus(session);
        RefreshMacroPill();
        Log.Info($"Macro \"{DisplayName(macro)}\" ({total} passos) em {session.Entry.Name}.");
        var watch = Stopwatch.StartNew();
        try
        {
            await runner.RunAsync(_macroCts.Token);
            Notify($"Macro \"{DisplayName(macro)}\" concluída: {total} {(total == 1 ? "passo" : "passos")} em {Elapsed(watch.Elapsed)}.", false);
        }
        catch (OperationCanceledException)
        {
            Notify($"Macro \"{DisplayName(macro)}\" parada no passo {_macroProgress?.Step ?? 1} de {total}.", true);
        }
        catch (MacroFailedException ex)
        {
            Log.Info($"Macro \"{DisplayName(macro)}\" parou: {ex.Message}");
            Notify($"Macro \"{DisplayName(macro)}\" parou no passo {_macroProgress?.Step ?? 1} de {total}: {ex.Message}", true);
        }
        finally
        {
            if (session.OutputObserved == (TerminalSession.OutputObserver)watcher.Append)
            {
                session.OutputObserved = null;
            }

            _macroCts.Dispose();
            _macroCts = null;
            _macroRunner = null;
            _runningMacro = null;
            _macroProgress = null;
            RefreshMacroPill();
        }
    }

    private void StopMacro() => _macroCts?.Cancel();

    // ------------------------------------------------------------------ gravar

    /// <summary>Começa a gravar os comandos executados no terminal ativo.</summary>
    private bool StartMacroRecording()
    {
        if (_recording is not null)
        {
            Notify("Já há uma gravação em andamento: clique em \"Gravando macro\" no topo para parar.", true);
            return false;
        }

        if (_macroRunner is not null)
        {
            Notify("Espere a macro em execução terminar (ou pare ela) antes de gravar.", true);
            return false;
        }

        if (_workspace.ActiveSession is not { State: TerminalState.Connected } session)
        {
            Notify("Abra ou selecione um terminal conectado para gravar a macro.", true);
            return false;
        }

        _recording = new MacroRecording(session);
        _workspace.CommandRan += OnRecordedCommand;
        _workspace.SessionsChanged += OnRecordingSessionsChanged;
        _workspace.Focus(session);
        RefreshMacroPill();
        Notify($"Gravando os comandos de {session.Entry.Name}. Execute-os no terminal e clique em \"Gravando macro\" no topo para parar.", false);
        return true;
    }

    private void OnRecordedCommand(TerminalSession session, string command)
    {
        // "^U": o Ctrl+U que o painel de comandos manda antes de cada comando, ecoado por shells que
        // não o tratam (cmd de servidor Windows). Não faz parte do comando.
        while (command.StartsWith("^U", StringComparison.Ordinal))
        {
            command = command[2..];
        }

        if (_recording is { } recording && recording.Session == session && command.Trim().Length > 0)
        {
            recording.Steps.Add(MacroStep.Command(command));
            RefreshMacroPill();
        }
    }

    /// <summary>A aba gravada foi fechada: encerra a gravação guardando o que já tinha.</summary>
    private void OnRecordingSessionsChanged()
    {
        if (_recording is { Session.IsDisposed: true })
        {
            StopMacroRecording(save: true);
        }
    }

    private void StopMacroRecording(bool save)
    {
        if (_recording is not { } recording)
        {
            return;
        }

        _recording = null;
        _workspace.CommandRan -= OnRecordedCommand;
        _workspace.SessionsChanged -= OnRecordingSessionsChanged;
        RefreshMacroPill();
        if (!save)
        {
            Notify("Gravação descartada.", false);
            return;
        }

        if (recording.Steps.Count == 0)
        {
            Notify("Nenhum comando foi gravado (só entram os comandos executados no prompt do terminal).", true);
            return;
        }

        var macro = new CommandMacro
        {
            Name = $"Gravada em {DateTime.Now:dd/MM HH:mm}",
            Description = $"Gravada no terminal {recording.Session.Entry.Name}",
            Steps = recording.Steps,
        };
        _settings.Macros.Add(macro);
        SaveSettings();
        _commands?.MacrosChanged(expand: true);
        Log.Info($"Macro gravada com {macro.Steps.Count} comandos.");
        OpenMacros(macro.Id, rename: true);
    }

    // ------------------------------------------------------------------ indicador no topo

    private void RefreshMacroPill()
    {
        if (_recording is { } recording)
        {
            MacroPillIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Record20;
            MacroPillIcon.Filled = true;
            MacroPillIcon.Foreground = (System.Windows.Media.Brush)FindResource("RdProd");
            var n = recording.Steps.Count;
            MacroPillText.Text = $"Gravando macro · {n} {(n == 1 ? "comando" : "comandos")}";
            MacroPill.ToolTip = $"Gravando os comandos de {recording.Session.Entry.Name}. Clique para parar e salvar.";
            MacroPill.Visibility = Visibility.Visible;
        }
        else if (_runningMacro is { } macro)
        {
            MacroPillIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Flash20;
            MacroPillIcon.Filled = true;
            MacroPillIcon.Foreground = (System.Windows.Media.Brush)FindResource("RdAccent");
            var p = _macroProgress;
            MacroPillText.Text = p is null ? $"Macro \"{Shorten(DisplayName(macro))}\"" : $"Macro \"{Shorten(DisplayName(macro))}\" · {p.Step}/{p.Total}";
            MacroPill.ToolTip = p is null ? "Começando..." : $"Passo {p.Step} de {p.Total}: {p.Current.Describe()}\n{Capitalize(p.Status)}.\n\nClique para parar ou pular a espera.";
            MacroPill.Visibility = Visibility.Visible;
        }
        else
        {
            MacroPill.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMacroPill(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MacroPill, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_recording is not null)
        {
            menu.Items.Add(MenuItem("Parar e salvar a macro", () => StopMacroRecording(save: true)));
            menu.Items.Add(MenuItem("Descartar a gravação", () => StopMacroRecording(save: false)));
        }
        else if (_macroRunner is { } runner)
        {
            if (_macroProgress is { Waiting: true } progress)
            {
                menu.Items.Add(MenuItem($"Não esperar mais o passo {progress.Step} (seguir)", runner.SkipWait));
            }

            menu.Items.Add(MenuItem("Parar a macro", StopMacro));
        }
        else
        {
            return;
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ apoio

    private static string DisplayName(CommandMacro macro) => string.IsNullOrWhiteSpace(macro.Name) ? "Macro sem nome" : macro.Name;

    private static string Elapsed(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{(int)t.TotalMinutes} min {t.Seconds} s";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
