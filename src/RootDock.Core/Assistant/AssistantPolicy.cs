using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootDock.Core.Assistant;

public enum AssistantMode
{
    /// <summary>Tudo o que altera algo ou executa comando pede confirmação.</summary>
    Ask,

    /// <summary>Roda sozinho, sem perguntar. Só a lista da regra da organização (banco e git) pede um clique.</summary>
    Auto,
}

public enum PermissionKind
{
    Allow,
    Ask,

    /// <summary>Comando da lista da organização: confirmado por uma pessoa em qualquer modo.</summary>
    AskDestructive,
}

public sealed record PermissionCheck(PermissionKind Kind, IReadOnlyList<string> Reasons);

/// <summary>
/// Decide o que o assistente pode fazer sem perguntar. No modo automático ele tem liberdade
/// total no servidor (apagar arquivos, reiniciar serviços, instalar e remover pacotes), com uma
/// única exceção: os comandos de banco de dados e de git que a regra da organização proíbe um
/// agente de rodar por conta própria. Esses pedem um clique de uma pessoa.
/// </summary>
public static partial class AssistantPolicy
{
    private sealed record Rule(Regex Pattern, string Reason);

    private static readonly Rule[] Rules =
    [
        new(SqlDrop(), "banco: DROP apaga banco, tabela, coluna ou esquema"),
        new(SqlTruncate(), "banco: TRUNCATE esvazia a tabela"),
        new(SqlDelete(), "banco: DELETE apaga linhas (rode antes o SELECT equivalente)"),
        new(SqlUpdateWithoutWhere(), "banco: UPDATE sem WHERE altera todas as linhas"),
        new(LaravelWipe(), "banco: recria, esvazia ou desfaz migrações (migrate:fresh/reset/rollback, db:wipe)"),
        new(GitForcePush(), "git: push --force reescreve o histórico remoto"),
        new(GitDeleteRemote(), "git: apaga uma branch no remoto"),
        new(GitResetHard(), "git: reset --hard descarta alterações e commits locais"),
        new(GitRewrite(), "git: reescreve o histórico (filter-branch / filter-repo)"),
        new(GitDiscard(), "git: descarta alterações não salvas (checkout -- . / restore . / clean -f)"),
        new(GitStash(), "git: apaga stashes (stash drop / clear)"),
    ];

    public static PermissionCheck Evaluate(ToolDefinition tool, JsonElement input, AssistantMode mode)
    {
        if (tool.ReadOnly)
        {
            return new(PermissionKind.Allow, []);
        }

        var reasons = DestructiveReasons(tool.Name, input);
        if (reasons.Count > 0)
        {
            return new(PermissionKind.AskDestructive, reasons);
        }

        return new(mode == AssistantMode.Auto ? PermissionKind.Allow : PermissionKind.Ask, []);
    }

    /// <summary>Motivos pelos quais o pedido cai na lista da organização (vazio = não cai).</summary>
    public static IReadOnlyList<string> DestructiveReasons(string toolName, JsonElement input)
    {
        var text = toolName switch
        {
            AssistantTools.RunCommand => Text(input, "command"),
            AssistantTools.TypeInTerminal => Text(input, "text"),
            _ => null,
        };
        return text is null ? [] : CommandReasons(text);
    }

    public static IReadOnlyList<string> CommandReasons(string command) =>
        string.IsNullOrWhiteSpace(command) ? [] : Rules.Where(r => r.Pattern.IsMatch(Joined(command))).Select(r => r.Reason).ToList();

    /// <summary>Linha continuada com barra invertida no fim vira uma linha só, como o shell a lê.</summary>
    private static string Joined(string command) => LineContinuation().Replace(command, " ");

    // As regras preferem pedir um clique a mais a deixar passar uma variação de escrita:
    // nomes entre crases ou aspas (escapadas ou não), opções juntas (-uf), caminhos "." e "./".

    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex LineContinuation();

    [GeneratedRegex(
        @"\bDROP\s+(?:DATABASE|SCHEMA|TABLE|VIEW|MATERIALIZED|INDEX|COLUMN|USER|ROLE|SEQUENCE|TYPE|EXTENSION|TRIGGER|PROCEDURE|FUNCTION|CONSTRAINT)\b" +
        @"|\bALTER\s+TABLE\b[^;]*\bDROP\b|\bdropdb\b|\bmysqladmin\b[^\n;|&]*\bdrop\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlDrop();

    [GeneratedRegex(@"\bTRUNCATE\s+(?:TABLE\s+)?[\\`""'\[\w$.]", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTruncate();

    [GeneratedRegex(@"\bDELETE\s+FROM\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlDelete();

    [GeneratedRegex(@"\bUPDATE\s+[\\`""'\[\]\w$.]+\s+SET\b(?![^;]*\bWHERE\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlUpdateWithoutWhere();

    [GeneratedRegex(@"\b(?:migrate:(?:fresh|reset|rollback|refresh)|db:wipe)\b")]
    private static partial Regex LaravelWipe();

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\bpush\b[^\n;|&]*(?:--force\b|--force-with-lease\b|\s-[a-zA-Z]*f[a-zA-Z]*\b|\s\+\S)")]
    private static partial Regex GitForcePush();

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\bpush\b[^\n;|&]*(?:--delete\b|\s-[a-zA-Z]*d[a-zA-Z]*\b|\s:\S)")]
    private static partial Regex GitDeleteRemote();

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\breset\b[^\n;|&]*--hard\b")]
    private static partial Regex GitResetHard();

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\b(?:filter-branch|filter-repo)\b")]
    private static partial Regex GitRewrite();

    [GeneratedRegex(
        @"\bgit\b[^\n;|&]*\b(?:" +
        @"checkout\b(?:\s+\S+)*?\s+\.{1,2}/?(?=[\s""';)]|$)" +
        @"|restore\b[^\n;|&]*\s\.{1,2}/?(?=[\s""';)]|$)" +
        @"|clean\b[^\n;|&]*(?:\s-[a-zA-Z]*f|--force\b))")]
    private static partial Regex GitDiscard();

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\bstash\s+(?:drop|clear)\b")]
    private static partial Regex GitStash();

    private static string? Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
