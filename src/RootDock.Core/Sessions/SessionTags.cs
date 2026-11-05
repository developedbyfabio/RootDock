using System.Globalization;
using System.Text;

namespace RootDock.Core.Sessions;

/// <summary>Regras das tags: sem espaços sobrando, até 32 letras, comparação sem maiúsculas nem acentos.</summary>
public static class SessionTags
{
    public const int MaxLength = 32;

    /// <summary>Tag limpa para gravar, ou null se ficou vazia.</summary>
    public static string? Clean(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var collapsed = string.Join(' ', tag.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimStart('#');
        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length > MaxLength ? collapsed[..MaxLength] : collapsed;
    }

    /// <summary>Chave de comparação: "Produção" = "PRODUCAO" = "produção".</summary>
    public static string Key(string tag)
    {
        var decomposed = tag.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static bool Same(string a, string b) => Key(a) == Key(b);

    public static bool Has(SessionEntry entry, string tag) => entry.Tags.Any(t => Same(t, tag));

    /// <summary>Adiciona sem repetir. Retorna true se mudou.</summary>
    public static bool Add(SessionEntry entry, string tag)
    {
        if (Clean(tag) is not { } clean || Has(entry, clean))
        {
            return false;
        }

        entry.Tags.Add(clean);
        return true;
    }

    public static bool Remove(SessionEntry entry, string tag) => entry.Tags.RemoveAll(t => Same(t, tag)) > 0;
}
