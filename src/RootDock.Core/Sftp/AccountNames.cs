using System.Globalization;

namespace RootDock.Core.Sftp;

/// <summary>Lê /etc/passwd ou /etc/group ("nome:x:id:..."): id → nome (o primeiro nome de cada id).</summary>
public static class AccountNames
{
    public static IReadOnlyDictionary<int, string> Parse(string text)
    {
        var names = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '+' || line[0] == '-')
            {
                continue; // comentário ou entrada NIS
            }

            var parts = line.Split(':');
            if (parts.Length >= 3 && parts[0].Length > 0 &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                names.TryAdd(id, parts[0]);
            }
        }

        return names;
    }
}
