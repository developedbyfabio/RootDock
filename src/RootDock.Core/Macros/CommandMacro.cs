using System.Text.Json.Serialization;
using RootDock.Core.Commands;

namespace RootDock.Core.Macros;

[JsonConverter(typeof(JsonStringEnumConverter<MacroStepKind>))]
public enum MacroStepKind
{
    /// <summary>Digita o texto e aperta Enter.</summary>
    Command,

    /// <summary>Espera alguns segundos.</summary>
    Wait,

    /// <summary>Espera um texto aparecer na saída (ex.: "password:" ou "[Y/n]"), com tempo máximo.</summary>
    WaitFor,
}

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; } = MacroStepKind.Command;

    /// <summary>Comando (Command) ou texto esperado (WaitFor).</summary>
    public string Text { get; set; } = "";

    /// <summary>Pausa (Wait) ou tempo máximo de espera (WaitFor).</summary>
    public double Seconds { get; set; }

    public static MacroStep Command(string text) => new() { Kind = MacroStepKind.Command, Text = text };

    public static MacroStep Pause(double seconds) => new() { Kind = MacroStepKind.Wait, Seconds = seconds };

    public static MacroStep Expect(string text, double timeoutSeconds = MacroRunner.DefaultExpectSeconds) =>
        new() { Kind = MacroStepKind.WaitFor, Text = text, Seconds = timeoutSeconds };

    /// <summary>Texto curto para listas: o comando, "pausa de 2 s" ou "esperar "password:"".</summary>
    public string Describe() => Kind switch
    {
        MacroStepKind.Wait => $"pausa de {Format(Seconds)} s",
        MacroStepKind.WaitFor => $"esperar \"{Text}\"",
        _ => Text,
    };

    internal static string Format(double seconds) => seconds.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
}

/// <summary>Sequência de passos executada no terminal ativo, como se fosse digitada.</summary>
public sealed class CommandMacro
{
    public string Id { get; set; } = "macro." + Guid.NewGuid().ToString("N")[..10];

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public List<MacroStep> Steps { get; set; } = [];

    /// <summary>
    /// Espera cada comando terminar (o prompt do shell voltar) antes de mandar o próximo. Desligado,
    /// os comandos vão um atrás do outro e o shell os enfileira.
    /// </summary>
    public bool WaitForPrompt { get; set; } = true;

    public IEnumerable<string> Commands => Steps.Where(s => s.Kind == MacroStepKind.Command).Select(s => s.Text);

    /// <summary>Algum comando altera ou apaga coisas (rm, kill, reboot...): pede confirmação antes de executar.</summary>
    public bool LooksDestructive => Commands.Any(CommandSafety.LooksDestructive);

    /// <summary>Resumo para o painel: os comandos, um por linha (pausas e esperas entre colchetes).</summary>
    public string Preview => string.Join("\n", Steps.Select(s => s.Kind == MacroStepKind.Command ? s.Text : $"[{s.Describe()}]"));

    public CommandMacro Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        WaitForPrompt = WaitForPrompt,
        Steps = Steps.Select(s => new MacroStep { Kind = s.Kind, Text = s.Text, Seconds = s.Seconds }).ToList(),
    };
}
