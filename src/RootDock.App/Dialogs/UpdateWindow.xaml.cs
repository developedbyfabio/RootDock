using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using RootDock.App.Services;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Ajuda → Verificar atualizações: situação, novidades da versão nova e "Reiniciar e atualizar".</summary>
public partial class UpdateWindow : FluentWindow
{
    private readonly UpdateService _updates;
    private readonly Action<bool> _saveAutomatic;
    private readonly Action _restart;
    private readonly bool _loading;

    private UpdateWindow(UpdateService updates, Action<bool> saveAutomatic, Action restart, bool checkNow)
    {
        InitializeComponent();
        _updates = updates;
        _saveAutomatic = saveAutomatic;
        _restart = restart;

        _loading = true;
        AutoBox.IsChecked = updates.Automatic;
        _loading = false;
        CurrentText.Text = updates.IsPortable ? $"{updates.CurrentVersion} (portátil)" : updates.CurrentVersion;
        SourceText.Text = UpdateService.RepositoryUrl.Replace("https://", "", StringComparison.Ordinal);

        updates.Changed += OnChanged;
        Closed += (_, _) => updates.Changed -= OnChanged;
        Loaded += (_, _) =>
        {
            if (checkNow && updates.Stage is UpdateStage.Idle or UpdateStage.UpToDate or UpdateStage.Failed)
            {
                _ = updates.CheckAsync();
            }
        };
        Render();
    }

    /// <param name="checkNow">Procura na hora (menu Ajuda); falso só mostra a situação (aviso da barra de título).</param>
    public static void ShowFor(Window owner, UpdateService updates, Action<bool> saveAutomatic, Action restart, bool checkNow) =>
        new UpdateWindow(updates, saveAutomatic, restart, checkNow) { Owner = owner }.ShowDialog();

    private void OnChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var u = _updates;
        StatusRing.Visibility = u.Stage == UpdateStage.Checking ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = u.Stage == UpdateStage.Checking ? Visibility.Collapsed : Visibility.Visible;
        DownloadBar.Visibility = u.Stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Value = u.Progress;
        RestartButton.Visibility = u.Stage == UpdateStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = u.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = u.Stage is not (UpdateStage.Checking or UpdateStage.Downloading);
        ReleasesButton.Visibility = u.IsSupported ? Visibility.Collapsed : Visibility.Visible;
        AutoBox.IsEnabled = u.IsSupported;
        AutoHint.Visibility = u.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        IsDefaultButton(u.Stage == UpdateStage.Ready);

        var (symbol, brush, title, detail) = u.Stage switch
        {
            UpdateStage.Unavailable => (SymbolRegular.Info24, "RdTextDim",
                "Atualização automática indisponível nesta cópia",
                "Ela funciona no RootDock instalado pelo RootDock-win-Setup.exe, que fica na página de versões do GitHub. " +
                $"Esta cópia está sendo executada de {AppContext.BaseDirectory.TrimEnd('\\')} (por exemplo, pelo Visual Studio)."),
            UpdateStage.Checking => (SymbolRegular.ArrowSync24, "RdText",
                "Procurando atualizações...", "Consultando as versões publicadas no GitHub."),
            UpdateStage.UpToDate => (SymbolRegular.CheckmarkCircle24, "RdDev",
                "O RootDock está atualizado", $"Você está usando a versão mais recente ({u.CurrentVersion})."),
            UpdateStage.Downloading => (SymbolRegular.ArrowDownload24, "RdAccent",
                $"Baixando a versão {u.AvailableVersion}... {u.Progress}%",
                "Baixando só o que mudou e preparando a instalação. Pode continuar trabalhando."),
            UpdateStage.Ready => (SymbolRegular.ArrowCircleUp24, "RdAccent",
                $"Versão {u.AvailableVersion} pronta para instalar",
                "Reinicie agora ou deixe para depois: ela é instalada sozinha quando você fechar o RootDock."),
            UpdateStage.Failed => (SymbolRegular.Warning24, "RdStaging",
                "Não foi possível verificar agora", u.Error ?? "Tente de novo em alguns minutos."),
            _ => (SymbolRegular.ArrowSync24, "RdText",
                "Atualizações do RootDock",
                u.Automatic ? "A verificação automática está ligada." : "A verificação automática está desligada; use \"Verificar agora\"."),
        };
        StatusIcon.Symbol = symbol;
        StatusIcon.SetResourceReference(ForegroundProperty, brush);
        TitleText.Text = title;
        DetailText.Text = detail;

        var showNotes = u.ReleaseNotes is not null && u.Stage is UpdateStage.Downloading or UpdateStage.Ready;
        NotesPanel.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
        if (showNotes)
        {
            NotesTitle.Text = $"Novidades da versão {u.AvailableVersion}";
            NotesText.Text = PlainNotes(u.ReleaseNotes!);
        }

        LastCheckText.Text = u.LastCheck switch
        {
            null => u.IsSupported ? "ainda não verificado nesta execução" : "—",
            { } at when at.Date == DateTime.Today => $"hoje às {at:HH:mm}",
            { } at => $"{at:dd/MM/yyyy HH:mm}",
        };
    }

    private void IsDefaultButton(bool restart)
    {
        RestartButton.IsDefault = restart;
        CheckButton.IsDefault = !restart && _updates.IsSupported;
    }

    /// <summary>Markdown simples das notas (títulos, listas, negrito, código) em texto legível.</summary>
    public static string PlainNotes(string markdown)
    {
        var text = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
            line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1•  ");
            line = Regex.Replace(line, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            text.Append(line).Append('\n'); // \n fixo: o colapso de linhas em branco abaixo depende disso
        }

        return Regex.Replace(text.ToString().Trim(), @"\n{3,}", "\n\n");
    }

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = AutoBox.IsChecked == true;
        _updates.SetAutomatic(enabled);
        _saveAutomatic(enabled);
        Render();
    }

    private async void OnCheck(object sender, RoutedEventArgs e) => await _updates.CheckAsync();

    private void OnOpenReleases(object sender, RoutedEventArgs e) => UpdateService.OpenReleasesPage();

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        Close();
        _restart();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
