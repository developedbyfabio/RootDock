using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace RootDock.App.Services;

/// <summary>
/// Ícones de arquivo e pasta como os do VS Code (Material Icon Theme, MIT): mesmo mapeamento
/// (nome do arquivo → extensão mais longa → linguagem → padrão) e as mesmas figuras, convertidas
/// do SVG por web/build-file-icons.mjs. Os ícones são montados sob demanda e reaproveitados.
/// </summary>
public static class FileIconTheme
{
    /// <summary>Tema claro ativo: usa as variantes "_light" (ícones escuros que somem no fundo claro).</summary>
    public static bool Light { get; set; }

    private static readonly Lazy<IconData?> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>Linguagens que o VS Code reconhece pelo nome (arquivos sem extensão conhecida).</summary>
    private static readonly Dictionary<string, string> LanguageByName = new(StringComparer.Ordinal)
    {
        [".bashrc"] = "shellscript", [".bash_profile"] = "shellscript", [".bash_aliases"] = "shellscript", [".bash_logout"] = "shellscript",
        [".profile"] = "shellscript", [".zshrc"] = "shellscript", [".zprofile"] = "shellscript", [".inputrc"] = "shellscript",
        [".bash_history"] = "plaintext", [".mysql_history"] = "plaintext", [".python_history"] = "plaintext", [".lesshst"] = "plaintext",
        [".wget-hsts"] = "plaintext", [".viminfo"] = "plaintext", ["hosts"] = "ini", ["fstab"] = "ini", ["crontab"] = "shellscript",
        ["sudoers"] = "ini", ["my.cnf"] = "ini", ["php.ini"] = "ini",
    };

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.Ordinal)
    {
        ["cnf"] = "ini", ["service"] = "ini", ["timer"] = "ini", ["socket"] = "ini", ["target"] = "ini", ["mount"] = "ini",
        ["out"] = "log", ["err"] = "log", ["pid"] = "plaintext",
    };

    /// <summary>Ícone do arquivo ou pasta (16×16 lógico), ou null se os ícones não carregaram.</summary>
    public static ImageSource? For(string name, bool isDirectory)
    {
        if (Data.Value is not { } data)
        {
            return null;
        }

        var icon = Resolve(data, name, isDirectory, Light);
        return Cache.GetOrAdd(icon, n => Build(data, n));
    }

    /// <summary>Carrega o mapeamento em segundo plano (o primeiro painel de arquivos já abre com ícones).</summary>
    public static void Warm() => _ = Task.Run(() => Data.Value);

    /// <summary>Nome do ícone (para testes e diagnóstico).</summary>
    public static string? IconName(string name, bool isDirectory, bool light = false) =>
        Data.Value is { } data ? Resolve(data, name, isDirectory, light) : null;

    /// <summary>Todos os ícones (para o teste que confere se cada figura é válida).</summary>
    public static IEnumerable<string> AllIconNames => Data.Value?.Icons.Keys ?? Enumerable.Empty<string>();

    /// <summary>Confere todas as figuras (teste): lista "ícone: erro" das formas que o WPF não aceita.</summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Data.Value is not { } data)
        {
            return ["ícones não carregaram"];
        }

        foreach (var (name, icon) in data.Icons)
        {
            if (icon.V is not { Length: 4 })
            {
                problems.Add($"{name}: sem viewBox");
            }

            foreach (var shape in icon.Sh)
            {
                try
                {
                    Geometry.Parse(shape.D);
                    if (shape.F is { ValueKind: JsonValueKind.String } f)
                    {
                        ColorConverter.ConvertFromString(f.GetString()!);
                    }
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
                {
                    problems.Add($"{name}: {ex.Message}");
                }
            }
        }

        return problems;
    }

    /// <summary>Ícone pelo nome do tema (ex.: "php", "folder-app").</summary>
    public static ImageSource? IconByName(string icon) => Data.Value is { } data ? Cache.GetOrAdd(icon, n => Build(data, n)) : null;

    private static string Resolve(IconData d, string name, bool isDirectory, bool light)
    {
        var lower = name.ToLowerInvariant();
        if (isDirectory)
        {
            return Lookup(d.FolderNames, d.Light?.FolderNames, lower, light) ?? d.Folder;
        }

        if (Lookup(d.FileNames, d.Light?.FileNames, lower, light) is { } byName)
        {
            return byName;
        }

        // Extensões da mais longa para a mais curta: "x.blade.php" → "blade.php", "php"; ".env" → "env".
        var parts = lower.Split('.');
        for (var i = 1; i < parts.Length; i++)
        {
            var extension = string.Join('.', parts[i..]);
            if (extension.Length > 0 && Lookup(d.FileExtensions, d.Light?.FileExtensions, extension, light) is { } byExtension)
            {
                return byExtension;
            }
        }

        var language = LanguageByName.GetValueOrDefault(lower) ?? (parts.Length > 1 ? LanguageByExtension.GetValueOrDefault(parts[^1]) : null);
        if (language is not null && Lookup(d.LanguageIds, d.Light?.LanguageIds, language, light) is { } byLanguage)
        {
            return byLanguage;
        }

        return d.File;
    }

    private static string? Lookup(Dictionary<string, string>? table, Dictionary<string, string>? lightTable, string key, bool light)
    {
        if (light && lightTable is not null && lightTable.TryGetValue(key, out var lightIcon))
        {
            return lightIcon;
        }

        return table is not null && table.TryGetValue(key, out var icon) ? icon : null;
    }

    private static IconData? Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RootDock.FileIcons.json");
            if (stream is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<IconData>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("Ícones de arquivo", ex);
            return null;
        }
    }

    private static ImageSource? Build(IconData data, string name)
    {
        if (!data.Icons.TryGetValue(name, out var icon) || icon.V is not { Length: 4 } v)
        {
            return null;
        }

        var box = new Rect(v[0], v[1], v[2], v[3]);
        var group = new DrawingGroup();
        // Moldura transparente do tamanho do viewBox: todos os ícones ocupam o mesmo espaço.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(box)));
        foreach (var shape in icon.Sh)
        {
            try
            {
                Geometry geometry = Geometry.Parse(shape.D);
                if (shape.M is { Length: 6 } m)
                {
                    geometry = new GeometryGroup { Children = { geometry }, Transform = new MatrixTransform(m[0], m[1], m[2], m[3], m[4], m[5]) };
                }

                var fill = Brush(shape.F, shape.O);
                Pen? pen = null;
                if (shape.S is { } stroke)
                {
                    var strokeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)) { Opacity = shape.O ?? 1 };
                    strokeBrush.Freeze();
                    pen = new Pen(strokeBrush, shape.Sw ?? 1)
                    {
                        StartLineCap = Cap(shape.Cap),
                        EndLineCap = Cap(shape.Cap),
                        LineJoin = shape.Join switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                    };
                }

                group.Children.Add(new GeometryDrawing(fill, pen, geometry));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
            {
                // forma inválida: o resto do ícone ainda aparece
            }
        }

        group.ClipGeometry = new RectangleGeometry(box);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static PenLineCap Cap(string? cap) => cap switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat };

    /// <summary>Cor sólida ("#RRGGBB") ou gradiente linear ({u, x1, y1, x2, y2, st: [[offset, cor, opacidade]]}).</summary>
    private static Brush? Brush(JsonElement? value, double? opacity)
    {
        if (value is not { } v || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        Brush brush;
        if (v.ValueKind == JsonValueKind.String)
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(v.GetString()!));
        }
        else
        {
            var gradient = new LinearGradientBrush
            {
                MappingMode = v.GetProperty("u").GetInt32() == 1 ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(v.GetProperty("x1").GetDouble(), v.GetProperty("y1").GetDouble()),
                EndPoint = new Point(v.GetProperty("x2").GetDouble(), v.GetProperty("y2").GetDouble()),
            };
            foreach (var stop in v.GetProperty("st").EnumerateArray())
            {
                var color = (Color)ColorConverter.ConvertFromString(stop[1].GetString()!);
                color.A = (byte)Math.Round(255 * Math.Clamp(stop[2].GetDouble(), 0, 1));
                gradient.GradientStops.Add(new GradientStop(color, stop[0].GetDouble()));
            }

            brush = gradient;
        }

        if (opacity is { } o)
        {
            brush.Opacity = o;
        }

        brush.Freeze();
        return brush;
    }

    private sealed class IconData
    {
        public string File { get; set; } = "file";

        public string Folder { get; set; } = "folder";

        public Dictionary<string, string>? FileExtensions { get; set; }

        public Dictionary<string, string>? FileNames { get; set; }

        public Dictionary<string, string>? FolderNames { get; set; }

        public Dictionary<string, string>? LanguageIds { get; set; }

        public LightTables? Light { get; set; }

        public Dictionary<string, IconDef> Icons { get; set; } = new();
    }

    private sealed class LightTables
    {
        public Dictionary<string, string>? FileExtensions { get; set; }

        public Dictionary<string, string>? FileNames { get; set; }

        public Dictionary<string, string>? FolderNames { get; set; }

        public Dictionary<string, string>? LanguageIds { get; set; }
    }

    private sealed class IconDef
    {
        public double[]? V { get; set; }

        public List<ShapeDef> Sh { get; set; } = [];
    }

    private sealed class ShapeDef
    {
        public string D { get; set; } = "";

        public JsonElement? F { get; set; }

        public double? O { get; set; }

        public string? S { get; set; }

        public double? Sw { get; set; }

        public string? Cap { get; set; }

        public string? Join { get; set; }

        public double[]? M { get; set; }
    }
}
