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

    /// <summary>Famílias em que o comando existe (null = as da categoria). Ver <see cref="OsFamily"/>.</summary>
    public List<string>? Os { get; init; }

    /// <summary>O que muda numa família (ex.: "rhel": dnf no lugar de apt). Campos ausentes ficam iguais.</summary>
    public Dictionary<string, CommandVariant>? Variants { get; init; }

    /// <summary>Windows "Executar" (Win+R): abre o programa no computador (ex.: ncpa.cpl), não digita no terminal.</summary>
    public bool Run { get; init; }

    /// <summary>Windows: só existe no PowerShell (no CMD vai dentro de powershell -Command).</summary>
    public bool PowerShell { get; init; }

    public bool IsCaution => string.Equals(Risk, "caution", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// O comando como fica numa família (com a variante aplicada), ou null se não existe nela.
    /// </summary>
    public CommandDefinition? For(string family, IReadOnlyList<string> categoryFamilies)
    {
        if (!(Os ?? categoryFamilies).Contains(family, StringComparer.Ordinal))
        {
            return null;
        }

        if (Variants is null || !Variants.TryGetValue(family, out var v))
        {
            return this;
        }

        var cmd = v.Cmd ?? Cmd;
        var fields = CommandTemplate.FieldNames(cmd);
        return new CommandDefinition
        {
            Id = Id,
            Title = v.Title ?? Title,
            Cmd = cmd,
            Description = v.Description ?? Description,
            Tip = v.Tip ?? Tip,
            Risk = v.Risk ?? Risk,
            Sudo = v.Sudo ?? Sudo,
            Keys = Keys,
            KeysLabel = KeysLabel,
            Params = v.Params ?? Params.Where(p => fields.Contains(p.Name)).ToList(),
            Tags = Tags,
            Run = Run,
            PowerShell = v.PowerShell ?? PowerShell,
        };
    }

    /// <summary>Texto para exibir: campos aparecem como &lt;rótulo&gt;.</summary>
    public string DisplayCommand =>
        Keys ? KeysLabel ?? Cmd : CommandTemplate.Placeholders.Replace(Cmd, m => $"<{Param(m.Groups[1].Value)?.Label.ToLowerInvariant() ?? m.Groups[1].Value}>");

    public CommandParameter? Param(string name) => Params.FirstOrDefault(p => p.Name == name);
}

/// <summary>Diferença de um comando numa família de sistema (só o que muda).</summary>
public sealed class CommandVariant
{
    public string? Cmd { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? Tip { get; init; }

    public string? Risk { get; init; }

    public bool? Sudo { get; init; }

    public bool? PowerShell { get; init; }

    public List<CommandParameter>? Params { get; init; }
}

/// <summary>
/// Famílias de sistema da biblioteca: comandos que mudam entre distribuições (gerenciador de pacotes,
/// firewall, caminhos de log) têm uma variante por família.
/// </summary>
public static class OsFamily
{
    /// <summary>Ubuntu, Debian, Mint.</summary>
    public const string Debian = "debian";

    /// <summary>Red Hat Enterprise Linux, CentOS, Rocky, AlmaLinux, Oracle Linux, Fedora.</summary>
    public const string Rhel = "rhel";

    /// <summary>SUSE Linux Enterprise (inclusive "for SAP Applications") e openSUSE.</summary>
    public const string Suse = "suse";

    public const string Windows = "windows";

    public static readonly IReadOnlyList<string> Linux = [Debian, Rhel, Suse];

    public static readonly IReadOnlyList<string> All = [Debian, Rhel, Suse, Windows];

    public static string Label(string family) => family switch
    {
        Rhel => "RHEL / CentOS / Rocky / Alma",
        Suse => "SUSE / SLES (SAP)",
        Windows => "Windows",
        _ => "Ubuntu / Debian",
    };

    /// <summary>
    /// ID e ID_LIKE do /etc/os-release (ex.: "rocky rhel centos fedora") → família; null se não for
    /// nenhuma das conhecidas (Alpine, Arch...).
    /// </summary>
    public static string? FromOsRelease(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return null;
        }

        var tokens = ids.ToLowerInvariant().Replace("\"", "").Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            switch (token)
            {
                case "ubuntu" or "debian" or "linuxmint" or "raspbian" or "pop" or "kali" or "elementary" or "zorin":
                    return Debian;
                case "rhel" or "centos" or "fedora" or "rocky" or "almalinux" or "ol" or "amzn" or "scientific" or "eurolinux" or "virtuozzo":
                    return Rhel;
                case "sles" or "sles_sap" or "sled" or "suse" or "opensuse" or "opensuse-leap" or "opensuse-tumbleweed":
                    return Suse;
            }
        }

        return null;
    }
}

public sealed class CommandCategory
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Icon { get; init; } = "Document20";

    public string? Description { get; init; }

    public List<CommandDefinition> Commands { get; init; } = [];

    /// <summary>Famílias da categoria (null = Linux: Debian, RHEL e SUSE).</summary>
    public List<string>? Os { get; init; }

    public IReadOnlyList<string> Families => Os ?? OsFamily.Linux;

    /// <summary>Os comandos desta categoria numa família (vazio se a categoria não vale nela).</summary>
    public IReadOnlyList<CommandDefinition> For(string family) =>
        Families.Contains(family, StringComparer.Ordinal)
            ? Commands.Select(c => c.For(family, Families)).OfType<CommandDefinition>().ToList()
            : [];
}

public sealed class CommandLibrary
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public List<CommandCategory> Categories { get; init; } = [];

    public IEnumerable<CommandDefinition> AllCommands => Categories.SelectMany(c => c.Commands);

    /// <summary>Cada comando como fica em cada família em que existe (para validar a biblioteca inteira).</summary>
    public IEnumerable<(string Family, CommandDefinition Command)> AllResolved() =>
        OsFamily.All.SelectMany(f => Categories.SelectMany(c => c.For(f)).Select(c => (f, c)));

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

    /// <param name="windows">Comando do Windows: aspas do PowerShell ou do CMD em vez das do shell Unix.</param>
    public static string Render(CommandDefinition command, IReadOnlyDictionary<string, string> values, bool windows = false)
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

            if (!windows)
            {
                return Quote(value);
            }

            if (command.PowerShell)
            {
                return QuotePowerShell(value);
            }

            // CMD: %VAR% e aspas seriam interpretados mesmo dentro de aspas.
            if (value.Contains('"') || value.Contains('%'))
            {
                throw new CommandRenderException($"\"{parameter.Label}\" não pode ter aspas nem %.");
            }

            return Plain.IsMatch(value) ? value : $"\"{value}\"";
        });
    }

    /// <summary>PowerShell: aspas simples (nada dentro é interpretado; ' vira '').</summary>
    public static string QuotePowerShell(string value) =>
        Plain.IsMatch(value) && !value.Contains('%') ? value : "'" + value.Replace("'", "''") + "'";

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
