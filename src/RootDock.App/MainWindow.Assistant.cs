using System.Windows;
using Microsoft.Web.WebView2.Core;
using RootDock.App.Assistant;
using RootDock.App.Services;

namespace RootDock.App;

/// <summary>Assistente (painel direito): chat de IA que age na sessão SSH em foco.</summary>
public partial class MainWindow
{
    private const double AssistantMinWidth = 340;
    private const double AssistantDefaultWidth = 460;

    /// <summary>O assistente pode crescer até deixar este tanto para o terminal.</summary>
    private const double TerminalMinWidth = 320;

    private AssistantService? _assistant;

    private bool IsAssistantPanelOpen => AssistantView.Visibility == Visibility.Visible;

    private void InitAssistant()
    {
        _assistant = new AssistantService(Dispatcher, _workspace, _settings.Assistant, SaveSettings, _vault, _paths.DataDirectory)
        {
            Macros = new AssistantMacroBook(this),
        };
        _assistant.CloseRequested += () => SetAssistantPanel(false);
        _workspace.AssistantToggleRequested += () => SetAssistantPanel(!IsAssistantPanelOpen);
        SizeChanged += (_, _) =>
        {
            // Janela menor: o assistente cede espaço para o terminal não sumir.
            if (IsAssistantPanelOpen && AssistantColumn.ActualWidth > ClampAssistantWidth(AssistantColumn.ActualWidth))
            {
                AssistantColumn.Width = new GridLength(ClampAssistantWidth(AssistantColumn.ActualWidth));
            }
        };
        AssistantView.SetTheme(AppThemes.PageMessage(AppThemes.Get(_settings.AppTheme)));
        if (_settings.Assistant.PanelOpen)
        {
            // Depois da janela e dos terminais: a página do chat não disputa o primeiro desenho.
            Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => SetAssistantPanel(true, focus: false));
        }
    }

    /// <summary>
    /// As macros vistas pelo assistente. As ferramentas rodam fora da thread da tela; ler e gravar
    /// acontece nela, para não disputar a lista com o painel de comandos e a janela de macros.
    /// </summary>
    private sealed class AssistantMacroBook(MainWindow window) : RootDock.Core.Assistant.IAssistantMacroBook
    {
        public IReadOnlyList<RootDock.Core.Macros.CommandMacro> List() =>
            window.Dispatcher.Invoke(() => window._settings.Macros.Select(m => m.Clone()).ToList());

        public void Save(RootDock.Core.Macros.CommandMacro macro) => window.Dispatcher.Invoke(() =>
        {
            var macros = window._settings.Macros;
            var index = macros.FindIndex(m => m.Id == macro.Id);
            if (index >= 0)
            {
                macros[index] = macro;
            }
            else
            {
                macros.Add(macro);
            }

            window.SaveSettings();
            window._commands?.MacrosChanged(expand: true);
            Dialogs.MacrosWindow.RefreshIfOpen();
            Log.Info($"Assistente: macro \"{macro.Name}\" {(index >= 0 ? "atualizada" : "criada")} ({macro.Steps.Count} passos).");
        });
    }

    private void SetAssistantPanel(bool open, bool focus = true)
    {
        if (_assistant is null)
        {
            return;
        }

        if (open)
        {
            AssistantColumn.MinWidth = AssistantMinWidth;
            AssistantColumn.Width = new GridLength(ClampAssistantWidth(_settings.Assistant.PanelWidth));
            AssistantView.Visibility = Visibility.Visible;
            AssistantSplitter.Visibility = Visibility.Visible;
            _ = StartAssistantAsync(focus);
        }
        else
        {
            RememberAssistantWidth();
            AssistantColumn.MinWidth = 0;
            AssistantColumn.Width = new GridLength(0);
            AssistantView.Visibility = Visibility.Collapsed;
            AssistantSplitter.Visibility = Visibility.Collapsed;
            _workspace.FocusTerminal();
        }

        _settings.Assistant.PanelOpen = open;
        SaveSettings();
        _workspace.SetAssistantPanelState(open);
    }

    private async Task StartAssistantAsync(bool focus)
    {
        try
        {
            var theme = AppThemes.Get(_settings.AppTheme);
            await AssistantView.EnsureStartedAsync(
                WebViewEnvironment ?? WebViewRuntime.CreateAsync(_paths.WebViewDirectory), _assistant!, AppThemes.Parse(theme.Palette.Panel));
            if (focus && IsAssistantPanelOpen)
            {
                AssistantView.FocusInput();
            }
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            SetAssistantPanel(false);
            Notify("Não foi possível abrir o assistente: " + ex.Message, true);
        }
    }

    /// <summary>Largura possível agora: o que sobra da janela depois da lateral, dos comandos e do mínimo do terminal.</summary>
    private double ClampAssistantWidth(double wanted)
    {
        var room = ActualWidth - SidebarColumn.ActualWidth - CommandsColumn.ActualWidth - TerminalMinWidth - 24;
        return Math.Clamp(wanted, AssistantMinWidth, Math.Max(AssistantMinWidth, room));
    }

    private void OnAssistantSplitterDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        AssistantColumn.Width = new GridLength(ClampAssistantWidth(AssistantColumn.ActualWidth - e.HorizontalChange));

    private void OnAssistantSplitterDone(object sender, EventArgs e)
    {
        RememberAssistantWidth();
        SaveSettings();
    }

    private void OnAssistantSplitterReset(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AssistantColumn.Width = new GridLength(ClampAssistantWidth(AssistantDefaultWidth));
        OnAssistantSplitterDone(sender, e);
    }

    private void RememberAssistantWidth()
    {
        if (IsAssistantPanelOpen && AssistantColumn.ActualWidth >= AssistantMinWidth)
        {
            _settings.Assistant.PanelWidth = AssistantColumn.ActualWidth;
        }
    }
}
