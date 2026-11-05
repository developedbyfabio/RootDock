using RootDock.Core.Sessions;

namespace RootDock.Core.LocalShell;

public sealed record LocalShellInfo(LocalShellKind Kind, string DisplayName, string CommandLine);

/// <summary>Shells locais instalados nesta máquina.</summary>
public static class LocalShells
{
    public static IReadOnlyList<LocalShellInfo> Available()
    {
        var list = new List<LocalShellInfo>();
        foreach (var kind in Enum.GetValues<LocalShellKind>())
        {
            var info = Resolve(kind);
            if (info is not null)
            {
                list.Add(info);
            }
        }

        return list;
    }

    public static LocalShellInfo? Resolve(LocalShellKind kind)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return kind switch
        {
            LocalShellKind.PowerShell => Exists(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "Windows PowerShell", "-NoLogo"),
            LocalShellKind.Pwsh => Exists(FindOnPath("pwsh.exe") ?? Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"), "PowerShell 7", "-NoLogo"),
            LocalShellKind.Cmd => Exists(Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(system, "cmd.exe"), "Prompt de Comando", null),
            LocalShellKind.Wsl => Exists(Path.Combine(system, "wsl.exe"), "WSL (Linux)", null),
            LocalShellKind.GitBash => Exists(Path.Combine(programFiles, "Git", "bin", "bash.exe"), "Git Bash", "--login -i"),
            _ => null,
        };

        LocalShellInfo? Exists(string path, string name, string? args) =>
            File.Exists(path) ? new LocalShellInfo(kind, name, args is null ? Quote(path) : $"{Quote(path)} {args}") : null;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static string? FindOnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Entrada inválida no PATH: ignora.
            }
        }

        return null;
    }
}
