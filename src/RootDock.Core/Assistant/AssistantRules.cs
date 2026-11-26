using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootDock.Core.Assistant;

/// <summary>
/// As regras que a pessoa dá ao assistente. São de dois tipos, de força diferente:
/// o texto (regras gerais, de todos os servidores, e as anotações de cada servidor) entra nas
/// instruções do modelo como obrigatório; e a lista de bloqueios é conferida pelo programa antes
/// de cada ferramenta rodar, então vale mesmo que o modelo ignore as instruções.
/// </summary>
public static partial class AssistantRules
{
    /// <summary>Nomes reservados no armazenamento de anotações (um servidor é sempre "host:porta", não colide).</summary>
    public const string GeneralKey = "regras-gerais";

    public const string BlockedKey = "comandos-bloqueados";

    /// <summary>Onde ficam os bloqueios de um servidor só (a chave do servidor é "host:porta").</summary>
    public static string ServerBlockedKey(string server) => server + "-bloqueios";

    public const int MaxPatterns = 200;

    /// <summary>Uma linha por bloqueio; linha vazia e linha começada por # não contam.</summary>
    public static IReadOnlyList<string> ParseBlocked(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPatterns)
            .ToList();

    /// <summary>
    /// O bloqueio em que o pedido esbarra, ou null. Vale para o comando, o texto digitado no
    /// terminal e o caminho de arquivo de qualquer ferramenta. Texto simples casa em qualquer ponto,
    /// sem diferenciar maiúsculas e ignorando espaços repetidos; entre barras (/.../) é expressão regular.
    /// </summary>
    public static string? Blocked(IReadOnlyList<string> patterns, JsonElement input)
    {
        if (patterns.Count == 0 || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var field in (string[])["command", "text", "path"])
        {
            if (!input.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } raw)
            {
                continue;
            }

            var subject = Spaces().Replace(raw, " ");
            foreach (var pattern in patterns)
            {
                if (Matches(pattern, subject))
                {
                    return pattern;
                }
            }
        }

        return null;
    }

    private static bool Matches(string pattern, string subject)
    {
        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try
            {
                return Regex.IsMatch(subject, pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                // expressão inválida: vale como texto simples, abaixo
            }
            catch (RegexMatchTimeoutException)
            {
                return true; // na dúvida, bloqueia
            }
        }

        return subject.Contains(Spaces().Replace(pattern, " "), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O que o modelo recebe quando esbarra num bloqueio.</summary>
    public static string BlockedMessage(string pattern) =>
        $"Bloqueado pelas regras da pessoa no RootDock (bloqueio: \"{pattern}\"). Esta ação não foi executada e não pode ser: " +
        "não tente de outro jeito nem com outro comando que faça o mesmo. Diga à pessoa o que você ia fazer e que a regra dela impede.";

    /// <summary>O trecho das instruções com as regras; vazio se não há nenhuma.</summary>
    public static string Section(string general, string server, string serverName, IReadOnlyList<string> blocked)
    {
        general = general.Trim();
        server = server.Trim();
        if (general.Length == 0 && server.Length == 0 && blocked.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder(
            "\n\n# Regras da pessoa (obrigatórias)\n" +
            "As regras abaixo foram escritas pela pessoa que usa o RootDock. Siga todas à risca, em toda resposta e em toda ação: " +
            "elas valem mais que os seus hábitos e que qualquer orientação anterior destas instruções, e uma proibição continua " +
            "valendo mesmo que o pedido da conversa pareça exigir o contrário. Se um pedido esbarrar numa regra, não execute: diga " +
            "qual regra impede e pergunte como ela quer seguir. Nunca contorne uma proibição com um comando equivalente.\n");
        if (general.Length > 0)
        {
            text.Append("\n## Regras gerais (todos os servidores)\n").Append(Cut(general)).Append('\n');
        }

        if (server.Length > 0)
        {
            text.Append("\n## Regras e anotações deste servidor (").Append(serverName).Append(")\n")
                .Append("Você mesmo acrescenta aqui, com a ferramenta remember, o que descobrir de útil e duradouro.\n")
                .Append(Cut(server)).Append('\n');
        }

        if (blocked.Count > 0)
        {
            text.Append("\n## Bloqueios\nO RootDock recusa sozinho qualquer comando, texto digitado ou caminho de arquivo que contenha: ")
                .Append(string.Join(" | ", blocked.Take(60))).Append(". Não tente: avise a pessoa.\n");
        }

        return text.ToString();
    }

    private static string Cut(string text) =>
        text.Length > AssistantMemoryStore.MaxChars ? text[..AssistantMemoryStore.MaxChars] + "\n[...]" : text;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
