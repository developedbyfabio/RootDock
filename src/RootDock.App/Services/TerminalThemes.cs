using RootDock.Core.Settings;

namespace RootDock.App.Services;

/// <summary>Esquema de cores do terminal (formato do xterm.js).</summary>
public sealed record TerminalTheme(string Id, string Name, IReadOnlyDictionary<string, string> Colors)
{
    public string Background => Colors["background"];

    public string Foreground => Colors["foreground"];

    /// <summary>Amostra para a tela de preferências: vermelho, verde, amarelo, azul, magenta, ciano.</summary>
    public IReadOnlyList<string> Swatches => new[] { "red", "green", "yellow", "blue", "magenta", "cyan" }.Select(k => Colors[k]).ToList();
}

/// <summary>Temas do terminal (cada tema da interface escolhe um; dá para trocar em Aparência do terminal).</summary>
public static class TerminalThemes
{
    public static readonly IReadOnlyList<TerminalTheme> All =
    [
        Theme("noite", "Noite (padrão)", "#11141a", "#d7dae0", "#7aa2f7", "#33466b",
            ["#1b1f27", "#f7768e", "#9ece6a", "#e0af68", "#7aa2f7", "#bb9af7", "#7dcfff", "#c0caf5"],
            ["#5b6170", "#ff8fa3", "#b5e08a", "#f0c88a", "#9ab8ff", "#cdb2ff", "#a4ddff", "#e6ebff"]),
        Theme("grafite", "Grafite", "#1a1b1e", "#d4d4d4", "#e6e6e6", "#3a3d44",
            ["#26282c", "#e06c6c", "#8fbf7f", "#d9b56c", "#6fa3d9", "#b48ead", "#6cc1c1", "#c8c8c8"],
            ["#6b6e75", "#f08a8a", "#a8d698", "#ecca85", "#8cbbea", "#c9a3c2", "#88d6d6", "#f0f0f0"]),
        Theme("oceano", "Oceano", "#0b1622", "#cfe3f2", "#4fc3f7", "#1d3a55",
            ["#13212f", "#ff6b81", "#7bd389", "#f5c26b", "#4fa8f7", "#a78bfa", "#4fd1c5", "#b9cfe0"],
            ["#4b6278", "#ff8fa0", "#9de3a8", "#ffd68f", "#79c0ff", "#c4b0ff", "#7ee6dc", "#eaf4fb"]),
        Theme("floresta", "Floresta", "#0f1a14", "#d3e5d6", "#8bd49c", "#24402f",
            ["#17261d", "#e8806e", "#8bd49c", "#d8c071", "#79a9d1", "#c39ac9", "#7cc7b2", "#c3d6c6"],
            ["#557060", "#f39b8b", "#a9e6b7", "#ead38c", "#9cc3e6", "#d6b4db", "#9cdcca", "#eef7ef"]),
        Theme("contraste", "Alto contraste", "#000000", "#ffffff", "#ffff00", "#264f78",
            ["#000000", "#ff5555", "#55ff55", "#ffff55", "#5c9cff", "#ff55ff", "#55ffff", "#e5e5e5"],
            ["#808080", "#ff7777", "#77ff77", "#ffff77", "#86b6ff", "#ff77ff", "#77ffff", "#ffffff"]),
        Theme("matrix", "Matrix", "#000000", "#00ff41", "#00ff41", "#004d14",
            ["#0a0f0a", "#ff5c57", "#00ff41", "#c5ff00", "#00c46a", "#00ff9c", "#3dffb3", "#a8ffbf"],
            ["#1f6b2f", "#ff8080", "#5cff7f", "#e1ff66", "#33e08c", "#66ffbf", "#80ffd0", "#e0ffe8"]),
        Theme("ambar", "Fósforo âmbar", "#0d0802", "#ffb000", "#ffcc33", "#4a3000",
            ["#1a1003", "#ff6a3d", "#ffb000", "#ffd060", "#d9902a", "#ff9a4d", "#ffc266", "#ffcc80"],
            ["#6b4a12", "#ff8a66", "#ffc640", "#ffe08c", "#f0b050", "#ffb880", "#ffd699", "#ffe6b3"]),
        Theme("fosforo", "Fósforo verde", "#031003", "#33ff66", "#66ff8f", "#0e4a1c",
            ["#062006", "#ff6b5c", "#33ff66", "#ccff66", "#2ecc71", "#66ffaa", "#5cffc8", "#b3ffc6"],
            ["#1f6b2f", "#ff8f80", "#66ff8f", "#e0ff8c", "#59e08f", "#8cffc0", "#8cffdb", "#e6ffec"]),
        Theme("claro", "Claro", "#ffffff", "#1f2328", "#2f6feb", "#b6d3fd",
            ["#24292f", "#cf222e", "#116329", "#9a6700", "#0550ae", "#8250df", "#1b7c83", "#6e7781"],
            ["#57606a", "#a40e26", "#1a7f37", "#7d4e00", "#0969da", "#a475f9", "#3192aa", "#8c959f"]),
    ];

    public static TerminalTheme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>Fontes de largura fixa comuns (só as instaladas aparecem na lista).</summary>
    public static readonly string[] MonospaceFonts =
    [
        "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Fira Mono", "Source Code Pro",
        "Hack", "DejaVu Sans Mono", "Ubuntu Mono", "Roboto Mono", "IBM Plex Mono", "Iosevka", "Lucida Console", "Courier New",
    ];

    /// <summary>Mensagem "settings" para a página com as preferências.</summary>
    public static object Message(TerminalAppearance a, int fontSize) => new
    {
        type = "settings",
        fontSize,
        fontFamily = $"\"{a.FontFamily}\", \"Cascadia Mono\", Consolas, \"Courier New\", monospace",
        lineHeight = Math.Clamp(a.LineHeight, 1.0, 2.0),
        cursorStyle = a.CursorStyle is "block" or "underline" ? a.CursorStyle : "bar",
        cursorBlink = a.CursorBlink,
        scrollback = Math.Clamp(a.Scrollback, 1000, 200_000),
        theme = Get(a.Theme).Colors,
        highlight = a.Highlight,
        copyOnSelect = a.CopyOnSelect,
        rightClickPaste = a.RightClickPaste,
    };

    private static TerminalTheme Theme(string id, string name, string bg, string fg, string cursor, string selection, string[] normal, string[] bright)
    {
        string[] names = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];
        var colors = new Dictionary<string, string>
        {
            ["background"] = bg,
            ["foreground"] = fg,
            ["cursor"] = cursor,
            ["cursorAccent"] = bg,
            ["selectionBackground"] = selection,
            ["selectionInactiveBackground"] = selection,
            ["overviewRulerBorder"] = bg,
        };
        for (var i = 0; i < 8; i++)
        {
            colors[names[i]] = normal[i];
            colors["bright" + char.ToUpperInvariant(names[i][0]) + names[i][1..]] = bright[i];
        }

        return new TerminalTheme(id, name, colors);
    }
}
