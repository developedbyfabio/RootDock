using System.Text.RegularExpressions;

namespace RootDock.Core.Commands;

/// <summary>
/// Padrões de comandos destrutivos ou irreversíveis. A biblioteca embutida não pode conter nenhum
/// (garantido por teste) e comandos do usuário que os contenham sempre pedem confirmação.
/// Segue a política da empresa para banco de dados e git.
/// </summary>
public static class CommandSafety
{
    public static IReadOnlyList<string> Patterns { get; } =
    [
        // Desligar/reiniciar só quando aparece como comando (não em caminhos como /var/run/reboot-required).
        @"(^|[;&|(]\s*|sudo\s+|systemctl\s+)(reboot|shutdown|poweroff|halt)(?![\w-])",
        @"\brm\s", @"\bmkfs", @"\bdd\s+if=", @"\binit\s+0\b",
        @"\bDROP\b", @"\bTRUNCATE\b", @"DELETE\s+FROM", @"UPDATE\s+\w+\s+SET\b(?![\s\S]*\bWHERE\b)",
        @"reset\s+--hard", @"push\s+(-f|--force)", @"force-with-lease", @"push\s+\S+\s+--delete",
        @"clean\s+-[a-z]*f", @"stash\s+(drop|clear)", @"filter-branch", @"filter-repo", @"checkout\s+--\s+\.", @"restore\s+\.",
        @"migrate:(fresh|reset|rollback)", @"db:wipe",
        @"compose\s+down", @"docker\s+(rm|rmi)\b", @"system\s+prune", @"volume\s+(rm|prune)",
        @"chmod\s+-R\s+777", @">\s*/dev/sd", @"--vacuum", @":\(\)\s*\{",
        // Windows (PowerShell e CMD)
        @"\bRemove-Item\b", @"(^|[;&|]\s*)(del|erase)\s", @"\b(rd|rmdir)\s+/s", @"\bformat\s+[a-z]:", @"\bFormat-Volume\b", @"\bClear-Disk\b",
        @"\b(Restart|Stop)-Computer\b", @"\bshutdown(\.exe)?\s+/[rsp]",
    ];

    private static readonly Regex[] Compiled =
        Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    public static bool LooksDestructive(string command) => Compiled.Any(r => r.IsMatch(command));
}
