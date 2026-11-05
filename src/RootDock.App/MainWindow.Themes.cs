using RootDock.App.Dialogs;
using RootDock.App.Services;

namespace RootDock.App;

/// <summary>Temas da interface (Exibir → Tema): aplica nas janelas, na página dos terminais e no editor.</summary>
public partial class MainWindow
{
    /// <summary>Antes da janela aparecer: cores do tema salvo (sem piscar o tema padrão).</summary>
    private void ApplyStartupTheme()
    {
        var theme = AppThemes.Get(_settings.AppTheme);
        AppThemes.ApplyToWpf(theme);
        FileIconTheme.Light = theme.IsLight;
        FileIconTheme.Warm();
        WebView.DefaultBackgroundColor = AppThemes.WebViewBackground(theme);
    }

    /// <summary>Troca o tema na hora. <paramref name="withTerminal"/>: usa também as cores de terminal do tema.</summary>
    private void ApplyTheme(string id, bool withTerminal)
    {
        var theme = AppThemes.Get(id);
        AppThemes.ApplyToWpf(theme);
        FileIconTheme.Light = theme.IsLight;
        WebView.DefaultBackgroundColor = AppThemes.WebViewBackground(theme);
        _settings.AppTheme = theme.Id;
        if (withTerminal)
        {
            _settings.Terminal.Theme = theme.TerminalTheme;
        }

        SaveSettings();
        _workspace.Send(AppThemes.PageMessage(theme));
        _workspace.Send(TerminalThemes.Message(_settings.Terminal, _settings.FontSize));
        FilesView.RefreshIcons();
        Notify($"Tema: {theme.Name}", false);
    }

    private void OpenThemeGallery() => ThemesWindow.ShowFor(this, _settings.AppTheme, ApplyTheme);
}
