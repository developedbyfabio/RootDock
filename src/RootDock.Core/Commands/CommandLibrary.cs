using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootDock.Core.Commands;

public sealed class CommandParameter
{
    public string Name { get; init; } = "";

    public string Label { get; init; } = "";

    public string? Placeholder { get; init; }

    public string? Default { get; init; }

    /// <summary>"text" (padrão, escapado para o shell) ou "number" (só dígitos).</summary>
    public string Type { get; init; } = "text";

    public bool IsNumber => string.Equals(Type, "number", StringComparison.OrdinalIgnoreCase);
}

public sealed class CommandDefinition
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>Modelo do comando. Campos no formato {{nome}}. Em atalhos (Keys), a sequência de teclas.</summary>
    public string Cmd { get; init; } = "";

    public string Description { get; init; } = "";

    public string? Tip { get; init; }

    /// <summary>"safe" (só consulta: roda ao clicar) ou "caution" (altera algo: pede confirmação).</summary>
    public string Risk { get; init; } = "safe";

    public bool Sudo { get; init; }

    /// <summary>Atalho de teclado (ex.: tmux Ctrl+B D): envia as teclas, sem Enter.</summary>
    public bool Keys { get; init; }

    public string? KeysLabel { get; init; }

    public List<CommandParameter> Params { get; init; } = [];

    public List<string> Tags { get; init; } = [];

    public bool IsCaution => string.Equals(Risk, "caution", StringComparison.OrdinalIgnoreCase);

    /// <summary>Texto para exibir: campos aparecem como &lt;rótulo&gt;.</summary>
    public string DisplayCommand =>
        Keys ? KeysLabel ?? Cmd : CommandTemplate.Placeholders.Replace(Cmd, m => $"<{Param(m.Groups[1].Value)?.Label.ToLowerInvariant() ?? m.Groups[1].Value}>");

    public CommandParameter? Param(string name) => Params.FirstOrDefault(p => p.Name == name);
}

public sealed class CommandCategory
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Icon { get; init; } = "Document20";

    public string? Description { get; init; }

    public List<CommandDefinition> Commands { get; init; } = [];
}

public sealed class CommandLibrary
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public List<CommandCategory> Categories { get; init; } = [];

    public IEnumerable<CommandDefinition> AllCommands => Categories.SelectMany(c => c.Commands);

    /// <summary>Biblioteca embutida no programa (Commands/commands.pt-BR.json).</summary>
    public static CommandLibrary LoadBuiltIn()
    {
        var assembly = typeof(CommandLibrary).Assembly;
        const string name = "RootDock.Commands.pt-BR.json";
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Recurso {name} não encontrado no RootDock.Core.");
        return JsonSerializer.Deserialize<CommandLibrary>(stream, Options) ?? new CommandLibrary();
    }

    public static CommandLibrary Parse(string json) =>
        JsonSerializer.Deserialize<CommandLibrary>(json, Options) ?? new CommandLibrary();
}

public sealed class CommandRenderException(string message) : Exception(message);

/// <summary>Preenche os campos {{nome}} de um comando com valores seguros para o shell.</summary>
public static class CommandTemplate
{
    public static readonly Regex Placeholders = new(@"\{\{([a-zA-Z0-9_]+)\}\}", RegexOptions.Compiled);

    // Caracteres que o shell não interpreta: o valor pode ir sem aspas (fica mais legível).
    private static readonly Regex Plain = new(@"^[A-Za-z0-9_./:@%+=,~-]+$", RegexOptions.Compiled);

    public static IReadOnlyList<string> FieldNames(string template) =>
        Placeholders.Matches(template).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static string Render(CommandDefinition command, IReadOnlyDictionary<string, string> values)
    {
        return Placeholders.Replace(command.Cmd, match =>
        {
            var name = match.Groups[1].Value;
            var parameter = command.Param(name) ?? new CommandParameter { Name = name, Label = name };
            values.TryGetValue(name, out var raw);
            var value = (raw ?? "").Trim();
            if (value.Length == 0)
            {
                throw new CommandRenderException($"Preencha \"{parameter.Label}\".");
            }

            if (parameter.IsNumber)
            {
                if (!value.All(char.IsAsciiDigit))
                {
                    throw new CommandRenderException($"\"{parameter.Label}\" aceita só números.");
                }

                return value;
            }

            if (value.StartsWith('-'))
            {
                throw new CommandRenderException($"\"{parameter.Label}\" não pode começar com \"-\".");
            }

            if (value.Contains('\n') || value.Contains('\r'))
            {
                throw new CommandRenderException($"\"{parameter.Label}\" não pode ter quebra de linha.");
            }

            return Quote(value);
        });
    }

    /// <summary>Sem aspas quando é seguro; senão entre aspas simples (nada dentro é interpretado).</summary>
    public static string Quote(string value)
    {
        // "~" só é expandido no começo e sem aspas: mantém "~/pasta" funcionando.
        if (Plain.IsMatch(value))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
