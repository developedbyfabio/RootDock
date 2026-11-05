using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Web.WebView2.Core;
using RootDock.App.Services;
using RootDock.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>Versão, ambiente (para suporte) e componentes de código aberto com as licenças.</summary>
public partial class AboutWindow : FluentWindow
{
    private static readonly (string Name, string License)[] Components =
    [
        ("xterm.js", "MIT"), ("Monaco Editor", "MIT"), ("noVNC", "MPL-2.0"), ("SSH.NET", "MIT"), ("BouncyCastle", "MIT"),
        ("FluentFTP", "MIT"), ("WPF-UI", "MIT"), ("CommunityToolkit.Mvvm", "MIT"), ("Material Icon Theme", "MIT"), ("Velopack", "MIT"), ("Microsoft Edge WebView2", "Microsoft"),
    ];

    private readonly List<(string Label, string Value)> _info = [];
    private readonly Action? _openUpdates;

    private AboutWindow(string dataDirectory, Action? openUpdates)
    {
        InitializeComponent();
        _openUpdates = openUpdates;
        var version = AppInfo.Version;
        VersionText.Text = $"Versão {version}";
        AuthorText.Text = AppInfo.Author;
        CopyrightText.Text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? $"© {AppInfo.Author}";
        UpdatesButton.Visibility = openUpdates is null ? Visibility.Collapsed : Visibility.Visible;

        string webView;
        try
        {
            webView = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "não encontrado";
        }
        catch (WebView2RuntimeNotFoundException)
        {
            webView = "não instalado";
        }

        _info.Add(("Versão", version));
        _info.Add(("Autor", AppInfo.Author));
        _info.Add(("Código-fonte", UpdateService.RepositoryUrl));
        _info.Add(("Atualizações", UpdateService.Current switch
        {
            null or { IsSupported: false } => "manuais (cópia fora do instalador)",
            { Stage: UpdateStage.Ready } u => $"versão {u.AvailableVersion} baixada, instala ao fechar",
            { Automatic: true } => "automáticas pelo GitHub",
            _ => "automáticas desligadas",
        }));
        _info.Add(("Windows", RuntimeInformation.OSDescription.Replace("Microsoft Windows", "", StringComparison.Ordinal).Trim()));
        _info.Add((".NET", RuntimeInformation.FrameworkDescription));
        _info.Add(("WebView2", webView));
        _info.Add(("Dados", dataDirectory));
        _info.Add(("Registros (logs)", AppPaths.Default.LogsDirectory));

        for (var i = 0; i < _info.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = _info[i].Label, Foreground = (System.Windows.Media.Brush)FindResource("RdTextDim"), Margin = new Thickness(0, 3, 0, 3) };
            var value = new TextBlock { Text = _info[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
            if (_info[i].Label is "Código-fonte")
            {
                var url = _info[i].Value;
                value.Inlines.Clear();
                var link = new Hyperlink(new Run(url.Replace("https://", "", StringComparison.Ordinal))) { ToolTip = "Abrir no navegador" };
                link.Click += (_, _) => UpdateService.OpenUrl(url);
                value.Inlines.Add(link);
            }
            else if (_info[i].Label is "Dados" or "Registros (logs)")
            {
                var path = _info[i].Value;
                value.Inlines.Clear();
                var link = new Hyperlink(new Run(path)) { ToolTip = "Abrir no Explorer" };
                link.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                value.Inlines.Add(link);
            }

            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InfoGrid.Children.Add(label);
            InfoGrid.Children.Add(value);
        }

        ComponentsText.Text = string.Join("   ·   ", Components.Select(c => $"{c.Name} ({c.License})"));
    }

    public static void ShowFor(Window owner, string dataDirectory, Action? openUpdates = null) =>
        new AboutWindow(dataDirectory, openUpdates) { Owner = owner }.ShowDialog();

    private void OnUpdates(object sender, RoutedEventArgs e)
    {
        Close();
        _openUpdates?.Invoke();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder("RootDock\n");
        foreach (var (label, value) in _info)
        {
            text.Append(label).Append(": ").AppendLine(value);
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (COMException)
        {
            // área de transferência ocupada: tenta de novo depois
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
