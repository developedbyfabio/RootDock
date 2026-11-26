using System.Text;

namespace RootDock.Core.Assistant;

/// <summary>
/// Diário de bordo do assistente: um arquivo de texto por servidor, na pasta de dados do RootDock,
/// com o que foi pedido e o que foi executado em cada rodada (comandos, arquivos alterados) e como
/// terminou. É escrito pelo programa, não pelo modelo, então registra o que de fato rodou. O fim
/// do diário entra em toda conversa nova daquele servidor: é assim que uma conversa sabe o que as
/// anteriores fizeram, mesmo sem reabrir o histórico.
/// </summary>
public sealed class AssistantRunbookStore(string directory)
{
    /// <summary>Acima disto o começo do arquivo é descartado (fica a metade mais recente).</summary>
    public const int MaxFileChars = 600_000;

    public const int MaxActionsPerEntry = 40;
    private const int MaxActionChars = 300;
    private const int MaxRequestChars = 240;
    private const int MaxOutcomeChars = 500;

    private readonly object _gate = new();

    /// <summary>Uma ação executada: o que foi (uma linha) e se deu certo.</summary>
    public sealed record Action(string What, bool Ok);

    /// <summary>Registra uma rodada. Sem ações, nada é escrito (conversa só de perguntas não é trabalho feito).</summary>
    public void Append(string server, DateTime when, string request, IReadOnlyList<Action> actions, string? outcome, bool finished)
    {
        if (actions.Count == 0)
        {
            return;
        }

        var entry = new StringBuilder()
            .Append("## ").Append(when.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" · ").Append(OneLine(request, MaxRequestChars)).Append('\n');
        foreach (var action in actions.Take(MaxActionsPerEntry))
        {
            entry.Append("- ").Append(OneLine(action.What, MaxActionChars)).Append(action.Ok ? "" : " (falhou)").Append('\n');
        }

        if (actions.Count > MaxActionsPerEntry)
        {
            entry.Append("- ... e mais ").Append(actions.Count - MaxActionsPerEntry).Append(" ações (a conversa completa está no histórico)\n");
        }

        if (!finished)
        {
            entry.Append("Situação: interrompido antes de terminar.\n");
        }
        else if (!string.IsNullOrWhiteSpace(outcome))
        {
            entry.Append("Resultado: ").Append(OneLine(outcome, MaxOutcomeChars)).Append('\n');
        }

        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = PathOf(server);
            var current = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var text = (current.Length > 0 ? current.TrimEnd() + "\n\n" : "") + entry;
            if (text.Length > MaxFileChars)
            {
                text = FromEntryStart(text[^(MaxFileChars / 2)..]);
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    /// <summary>As rodadas mais recentes, inteiras, até <paramref name="maxChars"/> caracteres.</summary>
    public string Tail(string server, int maxChars)
    {
        lock (_gate)
        {
            try
            {
                var path = PathOf(server);
                if (!File.Exists(path))
                {
                    return "";
                }

                var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                return text.Length <= maxChars ? text : FromEntryStart(text[^maxChars..]).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    public string PathOf(string server)
    {
        var name = new string(server.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return Path.Combine(directory, (name.Length > 0 ? name : "servidor") + ".md");
    }

    /// <summary>Corta no começo da primeira rodada inteira (um corte no meio deixaria ações sem o pedido).</summary>
    private static string FromEntryStart(string text)
    {
        if (text.StartsWith("## ", StringComparison.Ordinal))
        {
            return text;
        }

        var start = text.IndexOf("\n## ", StringComparison.Ordinal);
        return start >= 0 ? text[(start + 1)..] : text;
    }

    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > max ? line[..max] + "..." : line;
    }
}
