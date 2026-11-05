using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RootDock.App.Services;
using RootDock.Core.Settings;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Cores, fonte, cursor e comportamento do terminal, com prévia ao vivo.</summary>
public partial class TerminalSettingsWindow : FluentWindow
{
    private readonly int _initialSize;
    private bool _ready;

    /// <summary>Item da lista de temas (cores prontas para a tela).</summary>
    private sealed record ThemeItem(TerminalTheme Theme)
    {
        public string Name => Theme.Name;

        public Brush Background => Brush(Theme.Background);

        public Brush Foreground => Brush(Theme.Foreground);

        public IReadOnlyList<Brush> Swatches => Theme.Swatches.Select(Brush).ToList();
    }

    public TerminalAppearance Result { get; private set; } = new();

    public int ResultFontSize { get; private set; }

    private TerminalSettingsWindow(TerminalAppearance current, int fontSize)
    {
        InitializeComponent();
        _initialSize = fontSize;
        ThemesList.ItemsSource = TerminalThemes.All.Select(t => new ThemeItem(t)).ToList();
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = TerminalThemes.MonospaceFonts.Where(installed.Contains).ToList();
        if (!fonts.Contains(current.FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Insert(0, current.FontFamily);
        }

        FontBox.ItemsSource = fonts;
        Load(current, fontSize);
        SizeBox.ValueChanged += (_, _) => UpdatePreview();
        _ready = true;
        UpdatePreview();
    }

    /// <summary>Mostra a janela; null se cancelada.</summary>
    public static (TerminalAppearance Appearance, int FontSize)? Edit(Window owner, TerminalAppearance current, int fontSize)
    {
        var dialog = new TerminalSettingsWindow(current, fontSize) { Owner = owner };
        return dialog.ShowDialog() == true ? (dialog.Result, dialog.ResultFontSize) : null;
    }

    private void Load(TerminalAppearance a, int fontSize)
    {
        ThemesList.SelectedItem = ((IEnumerable<ThemeItem>)ThemesList.ItemsSource).FirstOrDefault(t => t.Theme.Id == a.Theme)
            ?? ((IEnumerable<ThemeItem>)ThemesList.ItemsSource).First();
        FontBox.SelectedItem = ((IEnumerable<string>)FontBox.ItemsSource).FirstOrDefault(f => string.Equals(f, a.FontFamily, StringComparison.OrdinalIgnoreCase));
        SizeBox.Value = fontSize;
        SelectByTag(LineHeightBox, a.LineHeight.ToString("0.0#", CultureInfo.InvariantCulture));
        (a.CursorStyle switch { "block" => CursorBlock, "underline" => CursorUnderline, _ => CursorBar }).IsChecked = true;
        BlinkBox.IsChecked = a.CursorBlink;
        SelectByTag(ScrollbackBox, a.Scrollback.ToString(CultureInfo.InvariantCulture));
        HighlightBox.IsChecked = a.Highlight;
        CopyOnSelectBox.IsChecked = a.CopyOnSelect;
        RightClickBox.IsChecked = a.RightClickPaste;
    }

    private static void SelectByTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[1];

    private TerminalAppearance Current() => new()
    {
        Theme = (ThemesList.SelectedItem as ThemeItem)?.Theme.Id ?? "noite",
        FontFamily = FontBox.SelectedItem as string ?? "Cascadia Mono",
        LineHeight = double.Parse((string)((ComboBoxItem)LineHeightBox.SelectedItem).Tag, CultureInfo.InvariantCulture),
        CursorStyle = CursorBlock.IsChecked == true ? "block" : CursorUnderline.IsChecked == true ? "underline" : "bar",
        CursorBlink = BlinkBox.IsChecked == true,
        Scrollback = int.Parse((string)((ComboBoxItem)ScrollbackBox.SelectedItem).Tag, CultureInfo.InvariantCulture),
        Highlight = HighlightBox.IsChecked == true,
        CopyOnSelect = CopyOnSelectBox.IsChecked == true,
        RightClickPaste = RightClickBox.IsChecked == true,
    };

    private void OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>Um trecho de terminal desenhado com as cores e a fonte escolhidas.</summary>
    private void UpdatePreview()
    {
        if (!_ready)
        {
            return;
        }

        var a = Current();
        var theme = TerminalThemes.Get(a.Theme);
        var c = theme.Colors;
        PreviewBox.Background = Brush(theme.Background);
        PreviewText.FontFamily = new FontFamily($"{a.FontFamily}, Cascadia Mono, Consolas");
        PreviewText.FontSize = Math.Clamp(SizeBox.Value ?? _initialSize, 8, 32) * 0.92;
        PreviewText.LineHeight = PreviewText.FontSize * a.LineHeight * 1.2;
        PreviewText.Inlines.Clear();

        void Add(string text, string color, bool bold = false, string? background = null)
        {
            var run = new Run(text) { Foreground = Brush(color), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            if (background is not null)
            {
                run.Background = Brush(background);
            }

            PreviewText.Inlines.Add(run);
        }

        void Line() => PreviewText.Inlines.Add(new LineBreak());

        Add("root@web01", c["brightGreen"], bold: true);
        Add(":", c["foreground"]);
        Add("~", c["brightBlue"], bold: true);
        Add("# ", c["foreground"]);
        Add("systemctl status nginx", c["foreground"]);
        Line();
        Add("● nginx.service - web server", c["foreground"]);
        Line();
        Add("   Active: ", c["foreground"]);
        Add("active (running)", a.Highlight ? "#a6e07a" : c["green"], bold: true);
        Line();
        Add("2026-11-05 03:12 ", c["cyan"]);
        if (a.Highlight)
        {
            Add("ERROR", "#ff7a93", background: "#351b24");
        }
        else
        {
            Add("ERROR", c["foreground"]);
        }

        Add(" upstream timed out", c["foreground"]);
        Line();
        Add("2026-11-05 03:13 ", c["cyan"]);
        if (a.Highlight)
        {
            Add("WARNING", "#f2c374", background: "#342a1a");
        }
        else
        {
            Add("WARNING", c["foreground"]);
        }

        Add(" disk 91% full", c["foreground"]);
        Line();
        Add("root@web01", c["brightGreen"], bold: true);
        Add(":", c["foreground"]);
        Add("~", c["brightBlue"], bold: true);
        Add("# ", c["foreground"]);
        var cursor = a.CursorStyle switch { "block" => "█", "underline" => "_", _ => "▏" };
        Add(cursor, c["cursor"]);
    }

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        _ready = false;
        Load(new TerminalAppearance(), 14);
        _ready = true;
        UpdatePreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = Current();
        ResultFontSize = (int)Math.Clamp(SizeBox.Value ?? _initialSize, 8, 32);
        DialogResult = true;
    }

    private static Brush Brush(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
