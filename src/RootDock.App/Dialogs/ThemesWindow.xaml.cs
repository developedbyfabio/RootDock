using System.Windows;
using System.Windows.Media;
using RootDock.App.Services;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Galeria de temas: miniatura de cada um com as cores reais; o clique aplica na hora.</summary>
public partial class ThemesWindow : FluentWindow
{
    private readonly Action<string, bool> _apply;
    private string _current;

    public sealed class Card(AppTheme theme, bool current)
    {
        private static Brush B(string hex)
        {
            var brush = new SolidColorBrush(AppThemes.Parse(hex));
            brush.Freeze();
            return brush;
        }

        private readonly TerminalTheme _term = TerminalThemes.Get(theme.TerminalTheme);

        public string Id => theme.Id;

        public string Name => theme.Name;

        public string Description => theme.Description;

        public bool IsCurrent { get; } = current;

        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ScanlinesVisibility => theme.Effects.Scanlines ? Visibility.Visible : Visibility.Collapsed;

        public Brush Background => B(theme.Palette.Background);

        public Brush Surface => B(theme.Palette.Surface);

        public Brush Border => B(theme.Palette.Border);

        public Brush Text => B(theme.Palette.Text);

        public Brush TextDim => B(theme.Palette.TextDim);

        public Brush Accent => B(theme.Palette.Accent);

        public Brush Selection => B(theme.Palette.Selection);

        public Brush TermBackground => B(_term.Background);

        public Brush TermForeground => B(_term.Foreground);

        public Brush TermGreen => B(_term.Colors["green"]);

        public Brush TermBlue => B(_term.Colors["blue"]);

        public Brush TermYellow => B(_term.Colors["yellow"]);

        public Brush TermRed => B(_term.Colors["red"]);
    }

    private ThemesWindow(string current, Action<string, bool> apply)
    {
        InitializeComponent();
        _current = current;
        _apply = apply;
        Fill();
    }

    public static void ShowFor(Window owner, string current, Action<string, bool> apply) =>
        new ThemesWindow(current, apply) { Owner = owner }.ShowDialog();

    private void Fill() => Cards.ItemsSource = AppThemes.All.Select(t => new Card(t, t.Id == _current)).ToList();

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            _current = id;
            _apply(id, WithTerminal.IsChecked == true);
            Fill();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
