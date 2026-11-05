using RootDock.Core.Sessions;

namespace RootDock.Core.Import;

/// <summary>Resultado da importação do ~/.ssh/config.</summary>
public sealed record SshConfigImport(IReadOnlyList<SessionEntry> Sessions, IReadOnlyList<string> Warnings);

/// <summary>
/// Lê o arquivo de configuração do OpenSSH (~/.ssh/config): cada "Host apelido" vira uma sessão
/// SSH com HostName, User, Port, IdentityFile e ProxyJump (quando o bastion também está no
/// arquivo). Padrões com curinga (Host *, Host *.empresa) só fornecem valores padrão.
/// </summary>
public static class SshConfigImporter
{
    public const string Folder = "OpenSSH (~/.ssh/config)";

    public static SshConfigImport Import(string path)
    {
        var warnings = new List<string>();
        var blocks = new List<(List<string> Patterns, Dictionary<string, string> Options)>();
        Parse(path, blocks, warnings, depth: 0);

        var aliases = new List<(string Alias, Dictionary<string, string> Options)>();
        foreach (var (patterns, options) in blocks)
        {
            foreach (var alias in patterns.Where(p => !p.Contains('*') && !p.Contains('?') && !p.StartsWith('!')))
            {
                aliases.Add((alias, options));
            }
        }

        // Como o ssh: o primeiro valor encontrado vale (blocos específicos e depois os com curinga).
        string? Option(string alias, string key)
        {
            foreach (var (patterns, options) in blocks)
            {
                if (BlockMatches(alias, patterns) && options.TryGetValue(key, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        var sessions = new List<SessionEntry>();
        var byAlias = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases.Select(a => a.Alias).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var host = Option(alias, "hostname") ?? alias;
            var portText = Option(alias, "port");
            var port = 22;
            if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            {
                warnings.Add($"{alias}: porta inválida \"{portText}\" (usando 22).");
                port = 22;
            }

            var entry = new SessionEntry
            {
                Name = alias,
                Kind = SessionKind.Ssh,
                Host = host.Replace("%h", alias, StringComparison.Ordinal),
                Port = port,
                Username = Option(alias, "user"),
                FolderPath = Folder,
                ImportedFrom = "OpenSSH",
                UseAgent = Option(alias, "identityagent") is { } agent && !agent.Equals("none", StringComparison.OrdinalIgnoreCase),
            };

            if (Option(alias, "identityfile") is { } identity)
            {
                var key = ExpandHome(identity.Trim('"'));
                if (File.Exists(key))
                {
                    entry.PrivateKeyPath = key;
                }
                else
                {
                    warnings.Add($"{alias}: chave {identity} não encontrada neste computador (a sessão fica sem chave).");
                }
            }

            if (entry.Username is not null)
            {
                entry.Logins.Add(entry.Username);
            }

            sessions.Add(entry);
            byAlias[alias] = entry;
        }

        // ProxyJump: liga a sessão ao bastion quando ele também é um Host do arquivo.
        foreach (var entry in sessions)
        {
            if (Option(entry.Name, "proxyjump") is not { } jump || jump.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var first = jump.Split(',')[0].Trim();
            var name = first.Contains('@') ? first[(first.IndexOf('@') + 1)..] : first;
            if (name.Contains(':'))
            {
                name = name[..name.IndexOf(':')];
            }

            if (byAlias.TryGetValue(name, out var bastion) && bastion != entry)
            {
                entry.JumpSessionId = bastion.Id;
                if (jump.Contains(','))
                {
                    warnings.Add($"{entry.Name}: ProxyJump com vários saltos ({jump}); importado só o primeiro ({name}). Os demais seguem pelo bastion dele.");
                }
            }
            else
            {
                warnings.Add($"{entry.Name}: bastion \"{first}\" não está no arquivo; configure o bastion na sessão depois de importar.");
            }
        }

        return new SshConfigImport(sessions, warnings);
    }

    private static void Parse(string path, List<(List<string>, Dictionary<string, string>)> blocks, List<string> warnings, int depth)
    {
        if (depth > 8 || !File.Exists(path))
        {
            if (depth == 0)
            {
                throw new FileNotFoundException($"Arquivo não encontrado: {path}");
            }

            return;
        }

        (List<string> Patterns, Dictionary<string, string> Options)? current = null;
        var inMatch = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var (key, value) = Split(line);
            if (key.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "host":
                    inMatch = false;
                    current = (Tokens(value), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    blocks.Add(current.Value);
                    continue;
                case "match":
                    inMatch = true; // condições de Match não são avaliadas: o bloco é ignorado
                    current = null;
                    warnings.Add("Blocos \"Match\" foram ignorados.");
                    continue;
                case "include":
                    foreach (var pattern in Tokens(value))
                    {
                        foreach (var file in ExpandInclude(pattern, Path.GetDirectoryName(path)!))
                        {
                            Parse(file, blocks, warnings, depth + 1);
                        }
                    }

                    continue;
            }

            if (inMatch)
            {
                continue;
            }

            if (current is null)
            {
                // Opções antes do primeiro Host valem para todos (como "Host *").
                current = (["*"], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                blocks.Add(current.Value);
            }

            current.Value.Options.TryAdd(key, value); // o primeiro valor vale
        }
    }

    private static (string Key, string Value) Split(string line)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=')
        {
            i++;
        }

        var key = line[..i].ToLowerInvariant();
        var rest = line[i..].TrimStart();
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart();
        }

        var hash = rest.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0)
        {
            rest = rest[..hash];
        }

        return (key, rest.Trim());
    }

    private static List<string> Tokens(string value) =>
        value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"')).ToList();

    /// <summary>
    /// Como o ssh: o bloco vale se algum padrão positivo casa e NENHUM negado (!padrão) casa.
    /// Ex.: "Host * !bastion" não aplica as opções ao bastion.
    /// </summary>
    private static bool BlockMatches(string alias, IEnumerable<string> patterns)
    {
        var matched = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Glob(alias, pattern[1..]))
                {
                    return false;
                }
            }
            else if (!matched && Glob(alias, pattern))
            {
                matched = true;
            }
        }

        return matched;
    }

    /// <summary>Curinga do ssh (* e ?), com a regex de cada padrão montada uma vez só.</summary>
    private static bool Glob(string alias, string pattern)
    {
        lock (PatternCache)
        {
            if (!PatternCache.TryGetValue(pattern, out var regex))
            {
                var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                regex = new System.Text.RegularExpressions.Regex(expression,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                PatternCache[pattern] = regex;
            }

            return regex.IsMatch(alias);
        }
    }

    private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> PatternCache = new(StringComparer.Ordinal);

    private static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')).Replace('/', Path.DirectorySeparatorChar)
            : path.Replace('/', Path.DirectorySeparatorChar);

    private static IEnumerable<string> ExpandInclude(string pattern, string baseDirectory)
    {
        var full = ExpandHome(pattern);
        if (!Path.IsPathRooted(full))
        {
            full = Path.Combine(baseDirectory, full);
        }

        var dir = Path.GetDirectoryName(full)!;
        var mask = Path.GetFileName(full);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, mask).Order() : [];
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
}
