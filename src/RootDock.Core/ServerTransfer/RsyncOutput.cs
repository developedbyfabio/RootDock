using System.Globalization;
using System.Text.RegularExpressions;

namespace RootDock.Core.ServerTransfer;

/// <summary>Uma linha de andamento do rsync (<c>--info=progress2</c>): bytes já copiados e percentual do total.</summary>
public readonly record struct RsyncProgress(long Bytes, int Percent);

/// <summary>Leitura do que o rsync escreve: versão, andamento e o significado dos códigos de saída.</summary>
public static partial class RsyncOutput
{
    /// <summary>Primeira versão com <c>--info=progress2</c> (andamento da transferência inteira).</summary>
    public static readonly Version MinimumVersion = new(3, 1, 0);

    // "    1,238,099,968  45%   12.34MB/s    0:01:23 (xfr#5, to-chk=169/396)"
    [GeneratedRegex(@"^\s*([0-9][0-9.,]*)\s+([0-9]{1,3})%\s")]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"version\s+([0-9]+)\.([0-9]+)(?:\.([0-9]+))?")]
    private static partial Regex VersionText();

    public static RsyncProgress? ParseProgress(string line)
    {
        var match = ProgressLine().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        return new RsyncProgress(bytes, Math.Min(100, percent));
    }

    /// <summary>"rsync  version 3.2.7  protocol version 31" → 3.2.7. Null se não for a saída do rsync.</summary>
    public static Version? ParseVersion(string text)
    {
        var match = VersionText().Match(text);
        if (!match.Success)
        {
            return null;
        }

        int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(Part(1), Part(2), Part(3));
    }

    /// <summary>Explicação curta do código de saída do rsync (0 = sucesso).</summary>
    public static string DescribeExit(int code) => code switch
    {
        0 => "Concluído.",
        1 or 2 => "O rsync deste servidor não aceita uma das opções usadas (versão antiga ou incompatível).",
        3 => "Pasta de origem ou de destino inacessível.",
        10 or 12 or 30 or 35 => "A conexão entre os dois servidores caiu durante a cópia.",
        11 => "Erro de leitura ou gravação (disco cheio ou sem permissão?).",
        20 => "Cópia interrompida.",
        23 => "Parte dos arquivos não pôde ser copiada (permissão negada ou erro de leitura).",
        24 => "Alguns arquivos sumiram da origem durante a cópia.",
        255 => "O servidor de origem não conseguiu conectar no de destino por SSH.",
        _ => $"O rsync terminou com o código {code}.",
    };
}
