using System.Text;
using System.Text.Json;
using RootDock.Core.Commands;
using RootDock.Core.Macros;

namespace RootDock.Core.Assistant;

/// <summary>As macros do RootDock (Ferramentas → Macros), como o assistente as lê e grava.</summary>
public interface IAssistantMacroBook
{
    /// <summary>Cópia das macros que existem hoje.</summary>
    IReadOnlyList<CommandMacro> List();

    /// <summary>Grava a macro: substitui a de mesmo Id ou acrescenta uma nova.</summary>
    void Save(CommandMacro macro);
}

/// <summary>O pedido do modelo para salvar uma macro não pode ser atendido; a mensagem volta para ele corrigir.</summary>
public sealed class MacroRequestException(string message) : Exception(message);

/// <summary>
/// Monta e confere a macro pedida pelo modelo (save_macro) e descreve as existentes (list_macros).
/// A conferência é rígida de propósito: uma macro salva vai ser digitada no terminal de qualquer
/// servidor com um clique, então o que entra aqui precisa ser exatamente o que a pessoa leu.
/// </summary>
public static class AssistantMacros
{
    public const int MaxSteps = 60;
    public const int MaxNameChars = 80;
    public const int MaxDescriptionChars = 300;
    public const int MaxCommandChars = 2000;
    public const double MaxSeconds = 3600;

    /// <summary>Constrói a macro a partir do pedido. <paramref name="existing"/> = as macros atuais.</summary>
    public static CommandMacro Build(JsonElement input, IReadOnlyList<CommandMacro> existing)
    {
        var name = (Text(input, "name") ?? "").ReplaceLineEndings(" ").Trim();
        if (name.Length == 0)
        {
            throw new MacroRequestException("Falta o nome da macro (name).");
        }

        if (name.Length > MaxNameChars)
        {
            throw new MacroRequestException($"O nome passa de {MaxNameChars} caracteres; use um nome curto e deixe o detalhe para a descrição.");
        }

        var replaceId = Text(input, "replace_id")?.Trim();
        CommandMacro? replaced = null;
        if (replaceId is { Length: > 0 })
        {
            replaced = existing.FirstOrDefault(m => m.Id == replaceId) ??
                       throw new MacroRequestException($"Não existe macro com o id \"{replaceId}\". Use list_macros para ver os ids, ou omita replace_id para criar uma nova.");
        }

        if (existing.FirstOrDefault(m => !ReferenceEquals(m, replaced) && string.Equals(m.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)) is { } clash)
        {
            throw new MacroRequestException(
                $"Já existe uma macro chamada \"{clash.Name}\" (id {clash.Id}). Para alterá-la, passe replace_id com esse id; para criar outra, use um nome diferente.");
        }

        var description = Text(input, "description")?.ReplaceLineEndings(" ").Trim();
        if (description is { Length: > MaxDescriptionChars })
        {
            description = description[..MaxDescriptionChars];
        }

        if (!input.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            throw new MacroRequestException("Faltam os passos (steps): uma lista com pelo menos um comando.");
        }

        if (list.GetArrayLength() > MaxSteps)
        {
            throw new MacroRequestException($"São {list.GetArrayLength()} passos; o limite é {MaxSteps}. Junte comandos relacionados ou divida em duas macros.");
        }

        var steps = new List<MacroStep>();
        var number = 0;
        foreach (var item in list.EnumerateArray())
        {
            number++;
            steps.Add(Step(item, number));
        }

        if (steps.All(s => s.Kind != MacroStepKind.Command))
        {
            throw new MacroRequestException("A macro precisa de pelo menos um passo do tipo command.");
        }

        if (steps[0].Kind == MacroStepKind.WaitFor)
        {
            throw new MacroRequestException("O primeiro passo não pode ser wait_for: ele espera um texto que aparece depois de um comando.");
        }

        var waitForPrompt = !input.TryGetProperty("wait_for_prompt", out var wait) || wait.ValueKind != JsonValueKind.False;
        return new CommandMacro
        {
            Id = replaced?.Id ?? new CommandMacro().Id,
            Name = name,
            Description = string.IsNullOrEmpty(description) ? null : description,
            WaitForPrompt = waitForPrompt,
            Steps = steps,
        };
    }

    private static MacroStep Step(JsonElement item, int number)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new MacroRequestException($"Passo {number}: cada passo é um objeto com kind e text (ou seconds).");
        }

        var kind = (Text(item, "kind") ?? "command").Trim().ToLowerInvariant();
        var text = Text(item, "text") ?? "";
        var seconds = item.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : (double?)null;
        if (seconds is { } given && (double.IsNaN(given) || given < 0 || given > MaxSeconds))
        {
            throw new MacroRequestException($"Passo {number}: seconds deve ficar entre 0 e {MaxSeconds:0}.");
        }

        switch (kind)
        {
            case "command":
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new MacroRequestException($"Passo {number}: falta o comando (text).");
                }

                if (text.Any(c => c is '\r' or '\n'))
                {
                    throw new MacroRequestException($"Passo {number}: cada passo é uma linha só. Separe em vários passos, ou junte com && ou ; na mesma linha.");
                }

                if (text.Any(char.IsControl))
                {
                    throw new MacroRequestException($"Passo {number}: o comando tem caracteres de controle (Tab, Esc...), que o terminal interpretaria. Use só texto.");
                }

                if (text.Length > MaxCommandChars)
                {
                    throw new MacroRequestException($"Passo {number}: o comando passa de {MaxCommandChars} caracteres. Para algo desse tamanho, grave um script no servidor e chame o script.");
                }

                return MacroStep.Command(text.Trim());

            case "wait":
                if (seconds is not > 0)
                {
                    throw new MacroRequestException($"Passo {number}: wait precisa de seconds maior que zero.");
                }

                return MacroStep.Pause(seconds.Value);

            case "wait_for":
                if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                {
                    throw new MacroRequestException($"Passo {number}: wait_for precisa do texto esperado (text), numa linha só.");
                }

                return MacroStep.Expect(text.Trim(), seconds is > 0 ? seconds.Value : MacroRunner.DefaultExpectSeconds);

            default:
                throw new MacroRequestException($"Passo {number}: kind \"{kind}\" não existe. Use command, wait ou wait_for.");
        }
    }

    /// <summary>Os passos como a pessoa os vê na confirmação e no cartão da conversa.</summary>
    public static string Preview(JsonElement input)
    {
        if (!input.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var text = new StringBuilder();
        if (Text(input, "description") is { Length: > 0 } description)
        {
            text.Append(description.Trim()).Append("\n\n");
        }

        var number = 0;
        foreach (var item in list.EnumerateArray())
        {
            number++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = (Text(item, "kind") ?? "command").Trim().ToLowerInvariant();
            var seconds = item.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
            var body = Text(item, "text") ?? "";
            text.Append(number).Append(". ").Append(kind switch
            {
                "wait" => $"[pausa de {seconds} s]",
                "wait_for" => $"[esperar aparecer \"{body}\"]",
                _ => (CommandSafety.LooksDestructive(body) ? "⚠ " : "") + body,
            }).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>O que o modelo recebe depois de salvar.</summary>
    public static string Saved(CommandMacro macro, bool replaced)
    {
        var steps = MacroRunner.Runnable(macro).Count;
        var text = new StringBuilder()
            .Append(replaced ? "Macro atualizada: " : "Macro criada: ").Append('"').Append(macro.Name).Append("\" (id ").Append(macro.Id).Append(", ")
            .Append(steps).Append(steps == 1 ? " passo). " : " passos). ")
            .Append("Ela já aparece no painel Comandos, em \"Minhas macros\", e em Ferramentas → Macros. A pessoa executa com um clique, ")
            .Append("no terminal da aba que estiver em foco; diga isso a ela.");
        var risky = macro.Commands.Where(CommandSafety.LooksDestructive).ToList();
        if (risky.Count > 0)
        {
            text.Append(" O RootDock vai pedir confirmação a cada execução, porque estes comandos alteram ou apagam coisas: ")
                .Append(string.Join(" | ", risky.Take(5))).Append('.');
        }

        return text.ToString();
    }

    /// <summary>As macros existentes, para o modelo não duplicar e saber os ids.</summary>
    public static string Describe(IReadOnlyList<CommandMacro> macros)
    {
        if (macros.Count == 0)
        {
            return "Não há macros ainda.";
        }

        var text = new StringBuilder();
        foreach (var macro in macros)
        {
            text.Append("id ").Append(macro.Id).Append(" · ").Append(string.IsNullOrWhiteSpace(macro.Name) ? "(sem nome)" : macro.Name);
            if (!string.IsNullOrWhiteSpace(macro.Description))
            {
                text.Append(" · ").Append(macro.Description);
            }

            text.Append(macro.WaitForPrompt ? "" : " · não espera o prompt entre os comandos").Append('\n');
            var number = 0;
            foreach (var step in macro.Steps)
            {
                number++;
                text.Append("  ").Append(number).Append(". ").Append(step.Kind switch
                {
                    MacroStepKind.Wait => $"[wait {MacroStep.Format(step.Seconds)} s]",
                    MacroStepKind.WaitFor => $"[wait_for \"{step.Text}\" até {MacroStep.Format(step.Seconds)} s]",
                    _ => step.Text,
                }).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string? Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
