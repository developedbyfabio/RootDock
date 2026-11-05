using System.Windows.Media;
using RootDock.Core.Sessions;

namespace RootDock.App.Services;

/// <summary>Etiqueta pronta para desenhar: fundo translúcido e texto na cor da tag.</summary>
public sealed record TagChip(string Name, Brush Background, Brush Foreground);

/// <summary>
/// Cor de cada tag: sempre a mesma para o mesmo nome (sem precisar cadastrar). Algumas palavras
/// comuns têm cor fixa (produção/crítico vermelho, homologação âmbar, backup verde-água...).
/// </summary>
public static class TagColors
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x7A, 0xA2, 0xF7), // azul
        Color.FromRgb(0x7D, 0xCF, 0xFF), // ciano
        Color.FromRgb(0xBB, 0x9A, 0xF7), // violeta
        Color.FromRgb(0xFF, 0x9E, 0x64), // laranja
        Color.FromRgb(0x9E, 0xCE, 0x6A), // verde
        Color.FromRgb(0xE0, 0xAF, 0x68), // âmbar
        Color.FromRgb(0x2A, 0xC3, 0xDE), // verde-água
        Color.FromRgb(0xFF, 0x79, 0xC6), // rosa
        Color.FromRgb(0xC0, 0xCA, 0xF5), // lavanda
        Color.FromRgb(0xB4, 0xF9, 0xF8), // menta
    ];

    private static readonly Dictionary<string, Color> Known = new(StringComparer.Ordinal)
    {
        ["PRODUCAO"] = Color.FromRgb(0xF7, 0x76, 0x8E),
        ["PROD"] = Color.FromRgb(0xF7, 0x76, 0x8E),
        ["CRITICO"] = Color.FromRgb(0xFF, 0x5F, 0x5F),
        ["HOMOLOGACAO"] = Color.FromRgb(0xE0, 0xAF, 0x68),
        ["HML"] = Color.FromRgb(0xE0, 0xAF, 0x68),
        ["STAGING"] = Color.FromRgb(0xE0, 0xAF, 0x68),
        ["DESENVOLVIMENTO"] = Color.FromRgb(0x9E, 0xCE, 0x6A),
        ["DEV"] = Color.FromRgb(0x9E, 0xCE, 0x6A),
        ["BACKUP"] = Color.FromRgb(0x2A, 0xC3, 0xDE),
        ["BANCO"] = Color.FromRgb(0xBB, 0x9A, 0xF7),
        ["DB"] = Color.FromRgb(0xBB, 0x9A, 0xF7),
    };

    private static readonly Dictionary<string, TagChip> Cache = new(StringComparer.Ordinal);

    public static Color ColorOf(string tag)
    {
        var key = SessionTags.Key(tag);
        if (Known.TryGetValue(key, out var known))
        {
            return known;
        }

        // Hash estável (string.GetHashCode muda a cada execução).
        var hash = 17u;
        foreach (var c in key)
        {
            hash = unchecked(hash * 31 + c);
        }

        return Palette[hash % (uint)Palette.Length];
    }

    public static TagChip Chip(string tag)
    {
        if (Cache.TryGetValue(tag, out var chip))
        {
            return chip;
        }

        var color = ColorOf(tag);
        var background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        var foreground = new SolidColorBrush(color);
        background.Freeze();
        foreground.Freeze();
        chip = new TagChip(tag, background, foreground);
        Cache[tag] = chip;
        return chip;
    }
}
