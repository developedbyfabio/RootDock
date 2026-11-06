namespace RootDock.Core.Monitoring;

public sealed record DiskUsage(string Mount, string FileSystem, long SizeKb, long UsedKb, long AvailableKb, int Percent);

public sealed record ProcessUsage(int Pid, string User, double Cpu, double Memory, string Command);

/// <summary>Uma leitura do estado do servidor. Valores nulos = não disponível neste sistema.</summary>
public sealed record ServerSnapshot
{
    public string? Hostname { get; init; }

    public string? OsName { get; init; }

    /// <summary>ID e ID_LIKE do /etc/os-release (ex.: "rocky rhel centos fedora"): família dos comandos.</summary>
    public string? OsId { get; init; }

    public string? Kernel { get; init; }

    public int Cores { get; init; }

    public string? IpAddresses { get; init; }

    public string? User { get; init; }

    public int UsersLoggedIn { get; init; }

    /// <summary>Sessões abertas do mesmo usuário (ex.: "root (x2)").</summary>
    public int UserSessions { get; init; }

    /// <summary>Uso de CPU (0–100). Nulo na primeira leitura (precisa de duas para calcular).</summary>
    public double? CpuPercent { get; init; }

    public double Load1 { get; init; }

    public double Load5 { get; init; }

    public double Load15 { get; init; }

    public long MemTotalKb { get; init; }

    public long MemAvailableKb { get; init; }

    public long SwapTotalKb { get; init; }

    public long SwapFreeKb { get; init; }

    public double UptimeSeconds { get; init; }

    public double? RxBytesPerSecond { get; init; }

    public double? TxBytesPerSecond { get; init; }

    public IReadOnlyList<DiskUsage> Disks { get; init; } = [];

    public IReadOnlyList<ProcessUsage> TopProcesses { get; init; } = [];

    /// <summary>Pasta atual do terminal desta conexão (para o painel de arquivos seguir).</summary>
    public string? TerminalDirectory { get; init; }

    /// <summary>Programa em primeiro plano no terminal (bash, vim, top...). Evita digitar comandos dentro de um editor.</summary>
    public string? TerminalForeground { get; init; }

    public long MemUsedKb => Math.Max(0, MemTotalKb - MemAvailableKb);

    public double MemPercent => MemTotalKb > 0 ? 100.0 * MemUsedKb / MemTotalKb : 0;

    public long SwapUsedKb => Math.Max(0, SwapTotalKb - SwapFreeKb);
}
