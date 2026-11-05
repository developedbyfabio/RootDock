using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RootDock.Core.History;

/// <summary>Um comando digitado num terminal.</summary>
public sealed record HistoryEntry(
    [property: JsonPropertyName("t")] DateTimeOffset Time,
    [property: JsonPropertyName("s")] Guid SessionId,
    [property: JsonPropertyName("n")] string SessionName,
    [property: JsonPropertyName("h")] string Host,
    [property: JsonPropertyName("c")] string Command);

/// <summary>
/// Histórico de comandos de todas as sessões, para buscar depois ("onde foi que eu rodei aquele
/// comando?"). Grava um JSON por linha no perfil do usuário; senhas e tokens que aparecem no
/// comando são mascarados antes de gravar (ver <see cref="HistorySanitizer"/>).
/// </summary>
public sealed class CommandHistory
{
    public const int MaxEntries = 20_000;

    private readonly string _file;
    private readonly List<HistoryEntry> _entries = new();
    private readonly object _gate = new();
    private bool _loaded;

    public CommandHistory(string file)
    {
        _file = file;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    /// <summary>Registra o comando (já mascarado). Comandos que começam com espaço não são gravados (como no bash).</summary>
    public HistoryEntry? Add(Guid sessionId, string sessionName, string host, string command, DateTimeOffset? when = null)
    {
        if (HistorySanitizer.Clean(command) is not { } clean)
        {
            return null;
        }

        var entry = new HistoryEntry(when ?? DateTimeOffset.Now, sessionId, sessionName, host, clean);
        lock (_gate)
        {
            EnsureLoaded();
            // O mesmo comando repetido em seguida na mesma sessão não ocupa duas linhas.
            if (_entries.Count > 0 && _entries[^1].SessionId == sessionId && _entries[^1].Command == clean)
            {
                return _entries[^1];
            }

            _entries.Add(entry);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, JsonSerializer.Serialize(entry) + "\n", Utf8);
                if (_entries.Count > MaxEntries + MaxEntries / 4)
                {
                    Compact();
                }
            }
            catch (IOException)
            {
                // histórico é conveniência: falha de disco não atrapalha o terminal
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entry;
    }

    /// <summary>
    /// Busca por palavras (todas precisam aparecer, sem diferenciar maiúsculas e acentos), do mais
    /// recente para o mais antigo. Repetições do mesmo comando aparecem uma vez (a mais recente).
    /// </summary>
    public IReadOnlyList<HistoryEntry> Search(string? query, Guid? sessionId = null, int limit = 500)
    {
        var words = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<(Guid, string)>();
        var result = new List<HistoryEntry>();
        lock (_gate)
        {
            EnsureLoaded();
            for (var i = _entries.Count - 1; i >= 0 && result.Count < limit; i--)
            {
                var e = _entries[i];
                if (sessionId is { } only && e.SessionId != only)
                {
                    continue;
                }

                if (words.Length > 0)
                {
                    var haystack = Normalize($"{e.Command} {e.SessionName} {e.Host}");
                    if (!words.All(haystack.Contains))
                    {
                        continue;
                    }
                }

                // Repetições somem só depois do filtro (o "ls" de srv-prod aparece mesmo se o
                // "ls" mais recente foi em srv-dev).
                if (!seen.Add((e.SessionId, e.Command)))
                {
                    continue;
                }

                result.Add(e);
            }
        }

        return result;
    }

    /// <summary>Apaga o histórico (de uma sessão ou de todas).</summary>
    public void Clear(Guid? sessionId = null)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (sessionId is { } only)
            {
                _entries.RemoveAll(e => e.SessionId == only);
            }
            else
            {
                _entries.Clear();
            }

            try
            {
                Compact();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Arquivo preso por outro programa: apaga direto para o histórico não voltar ao reabrir.
                try
                {
                    if (sessionId is null && File.Exists(_file))
                    {
                        File.Delete(_file);
                    }
                    else
                    {
                        throw;
                    }
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Não foi possível apagar o arquivo do histórico (está aberto em outro programa?).", inner);
                }
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_file, Utf8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line) is { } entry)
                    {
                        _entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // linha corrompida (ex.: queda de energia no meio da gravação): ignora
                }
            }
        }
        catch (IOException)
        {
        }

        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    /// <summary>Regrava só as entradas mais recentes (arquivo atômico: grava ao lado e troca).</summary>
    private void Compact()
    {
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        var temp = _file + ".tmp";
        var sb = new StringBuilder();
        foreach (var e in _entries)
        {
            sb.Append(JsonSerializer.Serialize(e)).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(temp, sb.ToString(), Utf8);
        File.Move(temp, _file, overwrite: true);
    }

    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Limpa o comando antes de ir para o histórico: senhas, tokens e chaves viram "••••".</summary>
public static partial class HistorySanitizer
{
    public const string Mask = "••••";
    public const int MaxLength = 4000;

    /// <summary>Comando pronto para gravar, ou null se não deve ser gravado.</summary>
    public static string? Clean(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.StartsWith(' ') || command.Length > MaxLength)
        {
            return null; // espaço no começo = "não guarde" (HISTCONTROL=ignorespace)
        }

        var text = command.TrimEnd();
        text = UrlCredentials().Replace(text, m => m.Groups[1].Value + Mask + "@");
        text = UserColonPassword().Replace(text, m => m.Groups[1].Value + Mask);
        text = ShortPasswordFlag().Replace(text, m => m.Groups[1].Value + Mask);
        text = Htpasswd().Replace(text, m => m.Groups[1].Value + Mask);
        text = ChpasswdEcho().Replace(text, m => m.Groups[1].Value + Mask + m.Groups[3].Value);
        text = SqlPassword().Replace(text, m => m.Groups[1].Value + "'" + Mask + "'");
        text = MysqlPassword().Replace(text, m => m.Groups[1].Value + Mask);
        text = LongPasswordOption().Replace(text, m => m.Groups[1].Value + Mask);
        text = SecretAssignment().Replace(text, m => m.Groups[1].Value + Mask);
        text = AuthorizationHeader().Replace(text, m => m.Groups[1].Value + Mask);
        text = IdentifiedBy().Replace(text, m => m.Groups[1].Value + "'" + Mask + "'");
        text = SshPass().Replace(text, m => m.Groups[1].Value + Mask);
        text = EchoToSudo().Replace(text, m => m.Groups[1].Value + Mask + m.Groups[3].Value);
        return text;
    }

    // https://usuario:senha@host → https://usuario:••••@host (a senha pode ter "/" e "@": vai até o último @)
    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://[^\s:/@]+:)\S+@(?=[^\s@]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    // curl -u usuario:senha / --user usuario:senha
    [GeneratedRegex(@"((?:\s-u|\s--user)\s*['""]?[^\s:'""]+:)[^\s'""]+")]
    private static partial Regex UserColonPassword();

    // docker login -p X, redis-cli -a X, mysqladmin --password X (o -p colado dos clientes MySQL já é tratado)
    [GeneratedRegex(@"(\b(?:docker\s+login|podman\s+login|helm\s+registry\s+login)\b[^|;&]*?\s-p\s+|\bredis-cli\b[^|;&]*?\s-a\s+)(?:'[^']*'|""[^""]*""|\S+)")]
    private static partial Regex ShortPasswordFlag();

    // htpasswd -b arquivo usuario senha
    [GeneratedRegex(@"(\bhtpasswd\s+(?:-\w+\s+)*-\w*b\w*\s+(?:-\w+\s+)*\S+\s+\S+\s+)(?:'[^']*'|""[^""]*""|\S+)")]
    private static partial Regex Htpasswd();

    // echo 'usuario:senha' | chpasswd
    [GeneratedRegex(@"(\becho\s+['""]?[^\s:'""|]+:)([^'""|\s]+)(['""]?\s*\|\s*(?:sudo\s+)?chpasswd)")]
    private static partial Regex ChpasswdEcho();

    // ALTER ROLE x WITH PASSWORD 'X' / SET PASSWORD = 'X' / ALTER USER ... PASSWORD 'X'
    [GeneratedRegex(@"(\bPASSWORD\s*(?:=\s*)?)(?:'[^']*'|""[^""]*"")", RegexOptions.IgnoreCase)]
    private static partial Regex SqlPassword();

    // mysql -pSenha / mysqldump -p'Senha' (o -p colado na senha é exclusivo desses clientes)
    [GeneratedRegex(@"(\b(?:mysql|mysqldump|mysqladmin|mariadb|mariadb-dump)\b[^|;&]*?\s-p)(?:'[^']*'|""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex MysqlPassword();

    // --password=xxx  --password xxx  --pass xxx  --token xxx  --secret xxx  --api-key xxx
    [GeneratedRegex(@"(--(?:password|passwd|pass|token|secret|api-?key|access-key|auth-token)(?:=|\s+))(?:'[^']*'|""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex LongPasswordOption();

    // MYSQL_PWD=xxx PGPASSWORD=xxx DB_PASSWORD=xxx AWS_SECRET_ACCESS_KEY=xxx GITHUB_TOKEN=xxx
    [GeneratedRegex(@"(\b\w*(?:PASSWORD|PASSWD|PWD|SECRET|TOKEN|API_?KEY|ACCESS_KEY)\w*=)(?:'[^']*'|""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(Authorization:\s*(?:Bearer|Basic|Token)\s+)[^\s'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(IDENTIFIED\s+(?:WITH\s+\w+\s+)?BY\s+)(?:'[^']*'|""[^""]*"")", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifiedBy();

    [GeneratedRegex(@"(\bsshpass\s+-p\s*)(?:'[^']*'|""[^""]*""|\S+)")]
    private static partial Regex SshPass();

    // echo 'senha' | sudo -S ...
    [GeneratedRegex(@"(\becho\s+['""]?)([^'""|]+)(['""]?\s*\|\s*sudo\s+-S)")]
    private static partial Regex EchoToSudo();
}
