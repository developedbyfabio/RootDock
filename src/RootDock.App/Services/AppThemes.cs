using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RootDock.App.Services;

/// <summary>Cores da interface de um tema (janelas, painéis, abas e editor).</summary>
public sealed record AppPalette(
    string Background, string Surface, string Panel, string Inset, string Hover, string HoverStrong, string Chip,
    string Border, string Guide, string GuideActive, string FocusBorder, string Track, string Text, string TextDim,
    string Accent, string AccentSoft, string OnAccent, string Selection, string SelectionInactive, string WarnBg,
    string Prod, string ProdSoft, string Staging, string Dev, string DevSoft, string Closed);

/// <summary>Cores de sintaxe do editor (Monaco).</summary>
public sealed record SyntaxPalette(
    string Comment, string Keyword, string String, string Number, string Variable, string Type, string Tag, string Constant, string Delimiter);

/// <summary>Efeitos especiais: linhas de tubo (CRT), brilho do texto, chuva do Matrix e fonte de terminal na interface.</summary>
public sealed record ThemeEffects(bool Scanlines = false, bool Glow = false, bool Rain = false, bool MonoUi = false);

/// <summary>Tema completo do RootDock: interface, editor, terminal e efeitos.</summary>
public sealed record AppTheme(
    string Id, string Name, string Description, bool IsLight, AppPalette Palette, SyntaxPalette Syntax, string TerminalTheme, ThemeEffects Effects)
{
    /// <summary>Tema "puro" do WPF-UI (escuro ou claro padrão): os controles dele não precisam ser tingidos.</summary>
    public bool Tinted { get; init; } = true;
}

/// <summary>
/// Temas da interface inteira. Trocar de tema substitui os pincéis Rd* (usados com DynamicResource
/// em todo o XAML), aplica o claro/escuro do WPF-UI e tinge os controles dele nos temas especiais.
/// A página dos terminais recebe as mesmas cores por mensagem (variáveis CSS e tema do editor).
/// </summary>
public static class AppThemes
{
    public static readonly IReadOnlyList<AppTheme> All =
    [
        new("escuro", "Escuro", "O padrão do RootDock: azul-acinzentado, confortável para o dia todo.", false,
            new("#11141A", "#171B23", "#13161D", "#0F1217", "#1C212B", "#232A38", "#262C38", "#262C38", "#323A4B", "#4A5670", "#33466B", "#1F2430",
                "#D7DAE0", "#8B93A5", "#7AA2F7", "#22304A", "#11141A", "#2A3A5C", "#232A38", "#3A2E1A",
                "#F7768E", "#2A1A20", "#E0AF68", "#9ECE6A", "#1E3A2A", "#5B6170"),
            new("#5B6170", "#BB9AF7", "#9ECE6A", "#FF9E64", "#7DCFFF", "#7AA2F7", "#F7768E", "#FF9E64", "#89DDFF"),
            "noite", new()) { Tinted = false },

        new("claro", "Claro", "Fundo branco e texto escuro, para ambientes iluminados.", true,
            new("#F3F4F6", "#FFFFFF", "#F7F8FA", "#EEF0F3", "#E9ECF1", "#DDE3EC", "#E3E7ED", "#D5DAE1", "#C9D0DA", "#9AA7B8", "#9DB8E8", "#E3E7ED",
                "#1F2328", "#5F6B7A", "#2F6FEB", "#DCE8FD", "#FFFFFF", "#D3E3FD", "#E5E9F0", "#FFF1D6",
                "#CF222E", "#FDE2E4", "#9A6700", "#1A7F37", "#DAFBE1", "#8C959F"),
            new("#6E7781", "#8250DF", "#0A3069", "#953800", "#0550AE", "#0969DA", "#116329", "#953800", "#57606A"),
            "claro", new()) { Tinted = false },

        new("grafite", "Grafite", "Cinza neutro, no estilo do VS Code Dark Modern.", false,
            new("#1F1F1F", "#181818", "#1B1B1B", "#141414", "#2A2D2E", "#37373D", "#313131", "#2B2B2B", "#3C3C3C", "#585858", "#0078D4", "#2B2B2B",
                "#CCCCCC", "#9D9D9D", "#3794FF", "#04395E", "#FFFFFF", "#04395E", "#37373D", "#3B2F18",
                "#F14C4C", "#3A1D1D", "#CCA700", "#89D185", "#1D3A20", "#6E6E6E"),
            new("#6A9955", "#C586C0", "#CE9178", "#B5CEA8", "#9CDCFE", "#4EC9B0", "#569CD6", "#4FC1FF", "#D4D4D4"),
            "grafite", new()),

        // Preto absoluto (bom em telas OLED): fundo, painéis e terminal em #000000; a separação vem de
        // contornos finos, não de tons de cinza. Texto quase branco e destaque azul vivo, com contraste
        // AA em tudo (o cinza secundário #8F8F8F dá 6,5:1 sobre o preto).
        new("black", "Black", "Preto absoluto: fundo e terminal pretos, contornos discretos e texto nítido.", false,
            new("#000000", "#000000", "#0A0A0A", "#0D0D0D", "#141414", "#1F1F1F", "#1A1A1A", "#232323", "#2E2E2E", "#4D4D4D", "#2F6FE0", "#1A1A1A",
                "#EDEDED", "#8F8F8F", "#4D94FF", "#0C1E3B", "#000000", "#1D3B6E", "#1C1C1C", "#2A1F05",
                "#FF5C6C", "#2B0A0F", "#F5B94A", "#5FD38D", "#0A2A17", "#5E5E5E"),
            new("#6A737D", "#C586C0", "#CE9178", "#B5CEA8", "#9CDCFE", "#4EC9B0", "#569CD6", "#4FC1FF", "#D4D4D4"),
            "black", new()),

        new("meia-noite", "Meia-noite", "Azul profundo com destaques em ciano.", false,
            new("#0B1220", "#0F172A", "#0D1526", "#091020", "#16213A", "#1C2A48", "#1E293B", "#1E293B", "#2A3A5A", "#3B5280", "#38BDF8", "#1E293B",
                "#E2E8F0", "#94A3B8", "#38BDF8", "#0C3048", "#0B1220", "#123A57", "#1C2A48", "#33280F",
                "#FB7185", "#3A1622", "#FBBF24", "#4ADE80", "#10331F", "#64748B"),
            new("#546E7A", "#C792EA", "#C3E88D", "#F78C6C", "#82AAFF", "#FFCB6B", "#F07178", "#F78C6C", "#89DDFF"),
            "oceano", new()),

        new("matrix", "Matrix", "Preto e verde neon, com a chuva de caracteres na tela inicial.", false,
            new("#000000", "#030A03", "#020802", "#000000", "#072207", "#0B330B", "#0A2A0A", "#0F3D0F", "#145214", "#1F7A1F", "#00FF41", "#0A2A0A",
                "#00FF41", "#00A82B", "#00FF41", "#003B10", "#000000", "#004D14", "#002B0B", "#1F2A00",
                "#FF5C57", "#2A0A0A", "#C5FF00", "#00FF41", "#003B10", "#1F6B2F"),
            new("#1F7A35", "#00FF41", "#9DFF9D", "#C5FF00", "#3DFFB3", "#00E676", "#00FF9C", "#C5FF00", "#00C46A"),
            "matrix", new(Glow: true, Rain: true, MonoUi: true)),

        new("ambar", "Computador antigo (âmbar)", "Monitor de fósforo âmbar dos anos 80, com linhas de tubo e brilho.", false,
            new("#0D0802", "#140C03", "#110A02", "#0A0601", "#261704", "#332006", "#2A1A05", "#3A2507", "#4D310A", "#7A4E10", "#FFB000", "#2A1A05",
                "#FFB000", "#B37A00", "#FFB000", "#3D2A00", "#0D0802", "#4A3000", "#2E1E03", "#2E1E03",
                "#FF6A3D", "#2E1206", "#FFD060", "#FFC640", "#3D2A00", "#6B4A12"),
            new("#8A5A00", "#FFCC33", "#FFE08C", "#FF9A4D", "#FFC266", "#FFB000", "#FF8A3D", "#FF9A4D", "#D9902A"),
            "ambar", new(Scanlines: true, Glow: true, MonoUi: true)),

        new("fosforo", "Computador antigo (verde)", "Terminal de fósforo verde, com linhas de tubo e brilho.", false,
            new("#031003", "#051805", "#041404", "#020C02", "#0A2A0A", "#0E380E", "#0B300B", "#124512", "#185A18", "#2A8A2A", "#33FF66", "#0B300B",
                "#33FF66", "#1FA344", "#33FF66", "#0B3A16", "#031003", "#0E4A1C", "#0A2E12", "#1F2A05",
                "#FF6B5C", "#2A0E0A", "#CCFF66", "#33FF66", "#0B3A16", "#1F6B2F"),
            new("#1E7A38", "#66FF8F", "#B3FFC6", "#CCFF66", "#5CFFC8", "#33FF66", "#2ECC71", "#CCFF66", "#2ECC71"),
            "fosforo", new(Scanlines: true, Glow: true, MonoUi: true)),
    ];

    public static AppTheme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>Pincéis do WPF-UI tingidos nos temas especiais (removidos ao voltar ao escuro/claro padrão).</summary>
    private static readonly string[] WpfUiOverrides =
    [
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "ApplicationBackgroundBrush",
        "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorTertiaryBrush", "ControlFillColorInputActiveBrush",
        "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush", "CardStrokeColorDefaultBrush", "SolidBackgroundFillColorBaseBrush",
        "SolidBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorTertiaryBrush", "SolidBackgroundFillColorQuarternaryBrush",
        "SurfaceStrokeColorFlyoutBrush", "LayerFillColorDefaultBrush", "DividerStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush",
    ];

    /// <summary>Aplica o tema nas janelas WPF (vale para as já abertas).</summary>
    public static void ApplyToWpf(AppTheme theme)
    {
        var p = theme.Palette;
        var baseTheme = theme.IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        if (ApplicationThemeManager.GetAppTheme() != baseTheme)
        {
            ApplicationThemeManager.Apply(baseTheme, WindowBackdropType.None, updateAccent: false);
        }

        var resources = Application.Current.Resources;
        void Set(string key, string hex) => resources[key] = Brush(hex);
        void SetColor(string key, Color color) => resources[key] = color;

        // Cor de destaque exata do tema (o WPF-UI clarearia: um verde neon viraria quase branco) e o
        // texto sobre ela com contraste (botão principal, caixas marcadas, seleção de texto).
        var accent = Parse(p.Accent);
        var hover = Mix(accent, theme.IsLight ? Colors.Black : Colors.White, 0.12);
        var pressed = Mix(accent, Colors.Black, 0.18);
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary" })
        {
            SetColor(key, accent);
            resources[key + "Brush"] = Brush(accent);
        }

        resources["AccentFillColorDefaultBrush"] = Brush(accent);
        resources["AccentFillColorSecondaryBrush"] = Brush(hover);
        resources["AccentFillColorTertiaryBrush"] = Brush(pressed);
        resources["AccentButtonBackground"] = Brush(accent);
        resources["AccentButtonBackgroundPointerOver"] = Brush(hover);
        resources["AccentButtonBackgroundPressed"] = Brush(pressed);
        resources["AccentControlElevationBorderBrush"] = Brush(pressed);
        resources["AccentTextFillColorPrimaryBrush"] = Brush(accent);
        resources["AccentTextFillColorSecondaryBrush"] = Brush(hover);
        resources["AccentTextFillColorTertiaryBrush"] = Brush(pressed);
        Set("AccentButtonForeground", p.OnAccent);
        Set("AccentButtonForegroundPointerOver", p.OnAccent);
        Set("AccentButtonForegroundPressed", p.OnAccent);
        Set("TextOnAccentFillColorPrimaryBrush", p.OnAccent);
        Set("TextOnAccentFillColorSecondaryBrush", p.OnAccent);
        Set("AccentFillColorSelectedTextBackgroundBrush", p.Selection);
        Set("RdBackground", p.Background);
        Set("RdSurface", p.Surface);
        Set("RdPanel", p.Panel);
        Set("RdInset", p.Inset);
        Set("RdHover", p.Hover);
        Set("RdHoverStrong", p.HoverStrong);
        Set("RdChip", p.Chip);
        Set("RdBorder", p.Border);
        Set("RdGuide", p.Guide);
        Set("RdGuideActive", p.GuideActive);
        Set("RdFocusBorder", p.FocusBorder);
        Set("RdTrack", p.Track);
        Set("RdText", p.Text);
        Set("RdTextDim", p.TextDim);
        Set("RdAccent", p.Accent);
        Set("RdAccentSoft", p.AccentSoft);
        Set("RdOnAccent", p.OnAccent);
        Set("RdSelection", p.Selection);
        Set("RdSelectionInactive", p.SelectionInactive);
        Set("RdWarnBg", p.WarnBg);
        Set("RdProd", p.Prod);
        Set("RdProdSoft", p.ProdSoft);
        Set("RdStaging", p.Staging);
        Set("RdDev", p.Dev);
        Set("RdDevSoft", p.DevSoft);

        if (!theme.Tinted)
        {
            foreach (var key in WpfUiOverrides)
            {
                resources.Remove(key);
            }

            return;
        }

        // Temas especiais: botões, caixas de texto e menus do WPF-UI com as cores do tema.
        Set("TextFillColorPrimaryBrush", p.Text);
        Set("TextFillColorSecondaryBrush", p.TextDim);
        Set("TextFillColorTertiaryBrush", p.TextDim);
        Set("ApplicationBackgroundBrush", p.Background);
        Set("ControlFillColorDefaultBrush", p.Hover);
        Set("ControlFillColorSecondaryBrush", p.HoverStrong);
        Set("ControlFillColorTertiaryBrush", p.Panel);
        Set("ControlFillColorInputActiveBrush", p.Inset);
        Set("ControlStrokeColorDefaultBrush", p.Border);
        Set("ControlStrokeColorSecondaryBrush", p.Border);
        Set("CardBackgroundFillColorDefaultBrush", p.Panel);
        Set("CardBackgroundFillColorSecondaryBrush", p.Surface);
        Set("CardStrokeColorDefaultBrush", p.Border);
        Set("SolidBackgroundFillColorBaseBrush", p.Background);
        Set("SolidBackgroundFillColorSecondaryBrush", p.Surface);
        Set("SolidBackgroundFillColorTertiaryBrush", p.Hover);
        Set("SolidBackgroundFillColorQuarternaryBrush", p.HoverStrong);
        Set("SurfaceStrokeColorFlyoutBrush", p.Border);
        Set("LayerFillColorDefaultBrush", p.Surface);
        Set("DividerStrokeColorDefaultBrush", p.Border);
        Set("SubtleFillColorSecondaryBrush", p.Hover);
    }

    /// <summary>Mensagem para a página: variáveis CSS, efeitos e tema do editor.</summary>
    public static object PageMessage(AppTheme theme)
    {
        var p = theme.Palette;
        var s = theme.Syntax;
        return new
        {
            type = "appTheme",
            id = theme.Id,
            light = theme.IsLight,
            vars = new Dictionary<string, string>
            {
                ["--bg"] = p.Background,
                ["--bg-elev"] = p.Surface,
                ["--bg-hover"] = p.Hover,
                ["--border"] = p.Border,
                ["--text"] = p.Text,
                ["--text-dim"] = p.TextDim,
                ["--accent"] = p.Accent,
                ["--prod"] = p.Prod,
                ["--staging"] = p.Staging,
                ["--dev"] = p.Dev,
                ["--closed"] = p.Closed,
                ["--on-accent"] = p.OnAccent,
                ["--faint"] = p.Guide,
                ["--inset"] = p.Inset,
                ["--panel"] = p.Panel,
                ["--selection"] = p.Selection,
            },
            effects = new { scanlines = theme.Effects.Scanlines, glow = theme.Effects.Glow, rain = theme.Effects.Rain, mono = theme.Effects.MonoUi },
            editor = new
            {
                background = p.Background,
                foreground = p.Text,
                surface = p.Surface,
                panel = p.Panel,
                border = p.Border,
                selection = p.Selection,
                selectionInactive = p.SelectionInactive,
                lineNumber = p.Guide,
                lineNumberActive = p.TextDim,
                accent = p.Accent,
                guide = p.Track,
                guideActive = p.Guide,
                inserted = p.Dev,
                removed = p.Prod,
                syntax = new { comment = s.Comment, keyword = s.Keyword, @string = s.String, number = s.Number, variable = s.Variable, type = s.Type, tag = s.Tag, constant = s.Constant, delimiter = s.Delimiter, warn = p.Staging, error = p.Prod, info = p.Accent, dim = p.TextDim },
            },
        };
    }

    /// <summary>Cor de fundo do WebView antes da página carregar (evita o "flash").</summary>
    public static System.Drawing.Color WebViewBackground(AppTheme theme)
    {
        var c = Parse(theme.Palette.Background);
        return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Mistura duas cores (amount = quanto de <paramref name="with"/>).</summary>
    public static Color Mix(Color color, Color with, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + ((with.R - color.R) * amount)),
        (byte)Math.Round(color.G + ((with.G - color.G) * amount)),
        (byte)Math.Round(color.B + ((with.B - color.B) * amount)));

    private static SolidColorBrush Brush(string hex) => Brush(Parse(hex));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
