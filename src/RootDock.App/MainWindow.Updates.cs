using System.Windows;
using System.Windows.Threading;
using RootDock.App.Dialogs;
using RootDock.App.Services;

namespace RootDock.App;

/// <summary>
/// Atualização automática na janela principal: aviso "Reiniciar para atualizar" na barra de título
/// quando uma versão nova termina de baixar, Ajuda → Verificar atualizações e o reinício com confirmação.
/// </summary>
public partial class MainWindow
{
    private UpdateService? Updates => UpdateService.Current;

    private void InitUpdates()
    {
        if (Updates is not { } updates)
        {
            return;
        }

        updates.SetAutomatic(_settings.AutoUpdate);
        updates.Changed += (_, _) => RefreshUpdatePill();
        updates.Ready += (_, _) => PromptUpdate();
        RefreshUpdatePill();
    }

    private bool _startupSettled;
    private string? _promptedVersion;
    private DispatcherTimer? _promptRetry;
    private string? _promptWaitReason;

    /// <summary>Fim da abertura (página pronta, importação e avisos): já dá para perguntar sobre atualização.</summary>
    private void StartupSettled()
    {
        _startupSettled = true;
        PromptUpdate(); // versão baixada antes (ou durante a abertura)
    }

    /// <summary>
    /// Versão nova baixada: pergunta se atualiza agora (uma vez por versão). Espera a janela estar
    /// livre: abertura concluída, janela ativa e sem outro diálogo na frente.
    /// </summary>
    private void PromptUpdate()
    {
        if (Updates is not { Stage: UpdateStage.Ready, AvailableVersion: { } version } updates
            || !updates.Automatic || updates.RestartRequested || _promptedVersion == version)
        {
            _promptRetry?.Stop();
            return;
        }

        var wait = !_startupSettled ? "abertura em andamento"
            : WindowState == WindowState.Minimized ? "janela minimizada"
            : OwnedWindows.Count > 0 ? "outra janela do RootDock aberta"
            : !IsForeground() ? "RootDock fora do primeiro plano"
            : null;
        if (wait is not null)
        {
            if (wait != _promptWaitReason)
            {
                _promptWaitReason = wait;
                Log.Info($"Atualização {version} pronta; a pergunta espera: {wait}.");
            }

            _promptRetry ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => PromptUpdate(), Dispatcher);
            _promptRetry.Start();
            return;
        }

        _promptRetry?.Stop();
        _promptWaitReason = null;
        _promptedVersion = version;
        Log.Info($"Perguntando se atualiza para {version}.");
        if (UpdatePromptWindow.Ask(this, updates, _workspace.OpenCount))
        {
            Log.Info("Escolheu atualizar agora.");
            RestartForUpdate();
        }
        else
        {
            Log.Info("Escolheu atualizar depois.");
            Notify($"Ok. A versão {version} será instalada quando você fechar o RootDock (ou clique em \"Reiniciar para atualizar\").", false);
        }
    }

    /// <summary>
    /// RootDock em primeiro plano, perguntando ao Windows. O IsActive do WPF fica falso quando o foco
    /// está na área dos terminais (WebView2, uma janela de outro processo dentro desta), e a
    /// pergunta esperava para sempre.
    /// </summary>
    private bool IsForeground()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        return hwnd != IntPtr.Zero && foreground != IntPtr.Zero && (foreground == hwnd || GetAncestor(foreground, 2 /* GA_ROOT */) == hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>
    /// A página dos terminais foi recarregada após uma falha do WebView2: reaplica tema e painel e
    /// descarta o que só existia nela (editores, RDP, VNC). Os terminais o workspace já reabriu.
    /// </summary>
    private void OnPageReloaded()
    {
        _workspace.Send(AppThemes.PageMessage(AppThemes.Get(_settings.AppTheme)));
        _workspace.Send(TerminalThemes.Message(_settings.Terminal, _settings.FontSize));
        _workspace.SetCommandsPanelState(_settings.CommandsPanelOpen);
        var editors = _workspace.Editors?.ForgetAll() ?? 0;
        _rdp.CloseAll();
        _vnc.CloseAll();
        Notify(editors > 0
            ? $"A área das abas foi recarregada após uma falha do WebView2. Terminais reabertos; {editors} editor(es), RDP e VNC foram fechados."
            : "A área das abas foi recarregada após uma falha do WebView2. Os terminais foram reabertos.", true);
    }

    /// <summary>Aviso na abertura logo depois de uma atualização.</summary>
    private void AnnounceUpdate()
    {
        if (UpdateService.RestartedVersion is { } version)
        {
            UpdateService.RestartedVersion = null;
            Log.Info($"Reaberto depois de atualizar para {version}.");
            Notify($"RootDock atualizado para a versão {version}.", false);
        }
    }

    private void RefreshUpdatePill()
    {
        var ready = Updates is { Stage: UpdateStage.Ready };
        UpdatePill.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready)
        {
            UpdatePill.ToolTip = $"A versão {Updates!.AvailableVersion} foi baixada. Clique para ver as novidades e reiniciar.";
        }
    }

    private void OnUpdatePill(object sender, RoutedEventArgs e) => OpenUpdates(checkNow: false);

    private void OpenUpdates(bool checkNow)
    {
        if (Updates is not { } updates)
        {
            return;
        }

        UpdateWindow.ShowFor(this, updates, enabled =>
        {
            _settings.AutoUpdate = enabled;
            SaveSettings();
        }, RestartForUpdate, checkNow);

        if (updates is { Stage: UpdateStage.Ready, AvailableVersion: { } seen })
        {
            _promptedVersion = seen; // acabou de ver a versão (e escolheu não reiniciar): não pergunta de novo
        }
    }

    /// <summary>Fecha o RootDock (com a confirmação de sempre se houver conexões) e abre de novo já atualizado.</summary>
    private void RestartForUpdate()
    {
        Updates?.RequestRestart();
        Close();
    }
}
