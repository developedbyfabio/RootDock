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
            // Depois que a mensagem da página terminar de ser tratada: a janela da imagem é modal.
            ShowImage = (bytes, menu) => Dispatcher.BeginInvoke(() =>
            {
                _ = menu ? Dialogs.ImageViewerWindow.ShowMenu(this, bytes) : Dialogs.ImageViewerWindow.Show(this, bytes);
            }),
        };
        _assistant.CloseRequested += () => SetAssistantPanel(false);
        _workspace.AssistantToggleRequested += () => SetAssistantPanel(!IsAssistantPanelOpen);
        _assistantWanted = _settings.Assistant.PanelWidth;
        SizeChanged += (_, _) => FitAssistantWidth();
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
            _assistantWanted = _settings.Assistant.PanelWidth;
            AssistantColumn.Width = new GridLength(ClampAssistantWidth(_assistantWanted));
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

    /// <summary>
    /// A largura que a pessoa escolheu para o assistente (arrastando o divisor). A coluna pode ficar
    /// menor que isso enquanto a janela não tem espaço, mas a escolha não se perde.
    /// </summary>
    private double _assistantWanted = AssistantDefaultWidth;

    /// <summary>
    /// Ajusta o assistente ao espaço de agora: cede quando a janela encolhe (o terminal não some) e
    /// volta à largura escolhida quando o espaço volta. Sem a volta, levar a janela para um monitor
    /// com outra escala (a janela passa por tamanhos intermediários no caminho) deixava o chat
    /// espremido no mínimo até a pessoa arrastar de novo.
    /// </summary>
    private void FitAssistantWidth()
    {
        if (!IsAssistantPanelOpen)
        {
            return;
        }

        var target = ClampAssistantWidth(_assistantWanted);
        if (Math.Abs(AssistantColumn.Width.Value - target) >= 0.5)
        {
            AssistantColumn.Width = new GridLength(target);
        }
    }

    /// <summary>Largura possível agora: o que sobra da janela depois da lateral, dos comandos e do mínimo do terminal.</summary>
    private double ClampAssistantWidth(double wanted)
    {
        var room = ActualWidth - SidebarColumn.ActualWidth - CommandsColumn.ActualWidth - TerminalMinWidth - 24;
        return Math.Clamp(wanted, AssistantMinWidth, Math.Max(AssistantMinWidth, room));
    }

    // Arrasto do divisor. A largura sai sempre da posição do mouse na janela em relação ao ponto
    // onde o arrasto começou: o deslocamento que o Thumb informa é medido a partir dele mesmo, e ele
    // anda junto com o painel, o que fazia a largura oscilar. A nova largura é aplicada uma vez por
    // quadro de tela, para os dois WebView2 (terminal e assistente) não receberem redimensionamentos
    // intermediários que nem chegam a ser desenhados.
    private double _assistantDragStartWidth;
    private double _assistantDragStartX;
    private double? _assistantDragWanted;

    private void OnAssistantSplitterStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _assistantDragStartWidth = AssistantColumn.ActualWidth;
        _assistantDragStartX = System.Windows.Input.Mouse.GetPosition(this).X;
        _assistantDragWanted = null;
        System.Windows.Media.CompositionTarget.Rendering += OnAssistantSplitterFrame;
    }

    private void OnAssistantSplitterDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        _assistantDragWanted = ClampAssistantWidth(_assistantDragStartWidth - (System.Windows.Input.Mouse.GetPosition(this).X - _assistantDragStartX));

    private void OnAssistantSplitterFrame(object? sender, EventArgs e)
    {
        if (_assistantDragWanted is { } wanted)
        {
            _assistantDragWanted = null;
            if (Math.Abs(AssistantColumn.Width.Value - wanted) >= 0.5)
            {
                AssistantColumn.Width = new GridLength(wanted);
            }
        }
    }

    private void OnAssistantSplitterDone(object sender, EventArgs e)
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnAssistantSplitterFrame;
        OnAssistantSplitterFrame(null, EventArgs.Empty); // a última posição do mouse
        UpdateLayout();
        _assistantWanted = AssistantColumn.ActualWidth; // a pessoa escolheu esta largura
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
        // Guarda a largura escolhida, e não a de agora (que pode estar cedida a uma janela estreita).
        if (IsAssistantPanelOpen && _assistantWanted >= AssistantMinWidth)
        {
            _settings.Assistant.PanelWidth = _assistantWanted;
        }
    }
}
