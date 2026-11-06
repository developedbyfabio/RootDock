using System.Text.Json.Serialization;

namespace RootDock.Core.Sftp;

/// <summary>Colunas da lista do painel de arquivos.</summary>
public enum FileColumn
{
    Name,
    Size,
    Modified,
    Owner,
    Group,
    Permissions,
}

/// <summary>
/// Larguras, colunas escondidas e ordem da lista do painel de arquivos (guardado em settings.json).
/// O Nome é automático até o usuário mexer nele: ocupa o espaço que sobra, com um mínimo legível.
/// </summary>
public sealed class FileColumnSettings
{
    public const double MinWidth = 36;
    public const double MinNameWidth = 80;
    public const double AutoNameMinWidth = 180;
    public const double MaxWidth = 1600;

    public static readonly IReadOnlyList<FileColumn> Optional =
        [FileColumn.Size, FileColumn.Modified, FileColumn.Owner, FileColumn.Group, FileColumn.Permissions];

    private static readonly Dictionary<FileColumn, double> Defaults = new()
    {
        [FileColumn.Size] = 84,
        [FileColumn.Modified] = 124,
        [FileColumn.Owner] = 84,
        [FileColumn.Group] = 84,
        [FileColumn.Permissions] = 96,
    };

    /// <summary>Largura fixa do Nome; null = automático (ocupa o que sobra).</summary>
    public double? NameWidth { get; set; }

    /// <summary>Larguras das outras colunas, pelo nome da coluna ("Size", "Modified"...).</summary>
    public Dictionary<string, double> Widths { get; set; } = [];

    /// <summary>Colunas escondidas (o Nome nunca some).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Ordem da lista (vale para todas as pastas e sessões).</summary>
    public string SortColumn { get; set; } = nameof(FileColumn.Name);

    public bool SortDescending { get; set; }

    public static double DefaultWidth(FileColumn column) => Defaults.GetValueOrDefault(column, 100);

    public bool IsVisible(FileColumn column) => column == FileColumn.Name || !Hidden.Contains(column.ToString());

    public void SetVisible(FileColumn column, bool visible)
    {
        if (column == FileColumn.Name)
        {
            return;
        }

        Hidden.RemoveAll(h => h == column.ToString());
        if (!visible)
        {
            Hidden.Add(column.ToString());
        }
    }

    /// <summary>Largura configurada (sem olhar se está visível). Para o Nome automático, devolve null.</summary>
    public double? ConfiguredWidth(FileColumn column) =>
        column == FileColumn.Name ? NameWidth : Widths.TryGetValue(column.ToString(), out var w) ? w : DefaultWidth(column);

    /// <summary>Largura na tela: 0 se escondida; Nome automático ocupa o que sobra de <paramref name="available"/>.</summary>
    public double WidthOf(FileColumn column, double available)
    {
        if (column == FileColumn.Name)
        {
            return NameWidth ?? AutoName(available);
        }

        return IsVisible(column) ? ConfiguredWidth(column)!.Value : 0;
    }

    /// <summary>Soma das colunas visíveis, menos o Nome.</summary>
    [JsonIgnore]
    public double OthersWidth => Optional.Sum(c => WidthOf(c, 0));

    public double TotalWidth(double available) => WidthOf(FileColumn.Name, available) + OthersWidth;

    public void SetWidth(FileColumn column, double width)
    {
        var min = column == FileColumn.Name ? MinNameWidth : MinWidth;
        var value = Math.Round(Math.Clamp(double.IsFinite(width) ? width : min, min, MaxWidth));
        if (column == FileColumn.Name)
        {
            NameWidth = value;
        }
        else
        {
            Widths[column.ToString()] = value;
        }
    }

    /// <summary>Nome volta a ocupar o espaço que sobra.</summary>
    public void AutoSizeName() => NameWidth = null;

    public void Reset()
    {
        NameWidth = null;
        Widths.Clear();
        Hidden.Clear();
    }

    /// <summary>Depois de ler o arquivo: descarta larguras inválidas e nomes de coluna desconhecidos.</summary>
    public void Sanitize()
    {
        Widths ??= [];
        Hidden ??= [];
        if (NameWidth is { } name && (!double.IsFinite(name) || name < MinNameWidth || name > MaxWidth))
        {
            NameWidth = null;
        }

        foreach (var key in Widths.Keys.ToList())
        {
            var known = Enum.TryParse<FileColumn>(key, out var column) && column != FileColumn.Name && column.ToString() == key;
            if (!known || !double.IsFinite(Widths[key]) || Widths[key] < MinWidth || Widths[key] > MaxWidth)
            {
                Widths.Remove(key);
            }
        }

        Hidden = Hidden
            .Where(h => Enum.TryParse<FileColumn>(h, out var c) && c != FileColumn.Name && c.ToString() == h)
            .Distinct()
            .ToList();
        if (!Enum.TryParse<FileColumn>(SortColumn, out _))
        {
            SortColumn = nameof(FileColumn.Name);
            SortDescending = false;
        }
    }

    private double AutoName(double available) =>
        Math.Max(AutoNameMinWidth, Math.Floor(available - OthersWidth));
}
