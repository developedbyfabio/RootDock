using System.Globalization;

namespace RootDock.Core.Monitoring;

/// <summary>
/// Interpreta os blocos produzidos pelo script de <see cref="ServerMonitor"/>.
/// Guarda os contadores anteriores para calcular CPU e tráfego de rede por diferença.
/// </summary>
public sealed class ServerSnapshotParser
{
    private static readonly string[] VirtualInterfacePrefixes = ["lo", "veth", "docker", "br-", "virbr", "cni", "flannel", "kube", "cali", "vxlan"];

    private string? _hostname;
    private string? _os;
    private string? _osId;
    private string? _kernel;
    private int _cores;
    private string? _ips;

    private ulong _prevCpuTotal;
    private ulong _prevCpuIdle;
    private ulong _prevRx;
    private ulong _prevTx;
    private double _prevUptime;

    public ServerSnapshot Parse(IReadOnlyList<string> lines)
    {
        string? user = null;
        int users = 0, userSessions = 0;
        double? cpu = null;
        double load1 = 0, load5 = 0, load15 = 0, uptime = 0;
        long memTotal = 0, memAvailable = 0, swapTotal = 0, swapFree = 0;
        ulong rx = 0, tx = 0;
        var hasNet = false;
        var disks = new List<DiskUsage>();
        var procs = new List<ProcessUsage>();
        string? cwd = null;
        string? foreground = null;
        ulong cpuTotal = 0, cpuIdle = 0;
        var hasCpu = false;

        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            var tag = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (tag)
            {
                case "@host": _hostname = Empty(value) ?? _hostname; break;
                case "@os": _os = Empty(value) ?? _os; break;
                case "@osid": _osId = Empty(value) ?? _osId; break;
                case "@kernel": _kernel = Empty(value) ?? _kernel; break;
                case "@cores": _cores = ParseInt(value) is > 0 and var c ? c : _cores; break;
                case "@ips": _ips = Empty(value) ?? _ips; break;
                case "@user": user = Empty(value); break;
                case "@users": users = ParseInt(value); break;
                case "@usersame": userSessions = ParseInt(value); break;
                case "@cpu":
                    hasCpu = TryParseCpu(value, out cpuTotal, out cpuIdle);
                    break;
                case "@load":
                    var loads = Split(value);
                    if (loads.Length >= 3)
                    {
                        load1 = ParseDouble(loads[0]);
                        load5 = ParseDouble(loads[1]);
                        load15 = ParseDouble(loads[2]);
                    }

                    break;
                case "@uptime":
                    var up = Split(value);
                    uptime = up.Length > 0 ? ParseDouble(up[0]) : 0;
                    break;
                case "@mem":
                    ParseMem(value, ref memTotal, ref memAvailable, ref swapTotal, ref swapFree);
                    break;
                case "@net":
                    if (TryParseNet(value, out var r, out var t))
                    {
                        rx += r;
                        tx += t;
                        hasNet = true;
                    }

                    break;
                case "@df":
                    if (TryParseDisk(value) is { } disk)
                    {
                        disks.Add(disk);
                    }

                    break;
                case "@proc":
                    if (TryParseProcess(value) is { } proc)
                    {
                        procs.Add(proc);
                    }

                    break;
                case "@cwd":
                    cwd = Empty(value) ?? cwd;
                    break;
                case "@fg":
                    foreground = Empty(value) ?? foreground;
                    break;
            }
        }

        if (hasCpu)
        {
            if (_prevCpuTotal > 0 && cpuTotal > _prevCpuTotal)
            {
                var dTotal = cpuTotal - _prevCpuTotal;
                var dIdle = cpuIdle >= _prevCpuIdle ? cpuIdle - _prevCpuIdle : 0;
                cpu = Math.Clamp(100.0 * (dTotal - Math.Min(dIdle, dTotal)) / dTotal, 0, 100);
            }

            _prevCpuTotal = cpuTotal;
            _prevCpuIdle = cpuIdle;
        }

        double? rxRate = null, txRate = null;
        if (hasNet && uptime > 0)
        {
            var dt = uptime - _prevUptime;
            if (_prevUptime > 0 && dt > 0.05 && rx >= _prevRx && tx >= _prevTx)
            {
                rxRate = (rx - _prevRx) / dt;
                txRate = (tx - _prevTx) / dt;
            }

            _prevRx = rx;
            _prevTx = tx;
        }

        if (uptime > 0)
        {
            _prevUptime = uptime;
        }

        return new ServerSnapshot
        {
            Hostname = _hostname,
            OsName = _os,
            OsId = _osId,
            Kernel = _kernel,
            Cores = _cores,
            IpAddresses = _ips,
            User = user,
            UsersLoggedIn = users,
            UserSessions = userSessions,
            CpuPercent = cpu,
            Load1 = load1,
            Load5 = load5,
            Load15 = load15,
            MemTotalKb = memTotal,
            MemAvailableKb = memAvailable,
            SwapTotalKb = swapTotal,
            SwapFreeKb = swapFree,
            UptimeSeconds = uptime,
            RxBytesPerSecond = rxRate,
            TxBytesPerSecond = txRate,
            Disks = disks,
            TopProcesses = procs,
            TerminalDirectory = cwd,
            TerminalForeground = foreground,
        };
    }

    // "cpu  user nice system idle iowait irq softirq steal guest guest_nice"
    private static bool TryParseCpu(string value, out ulong total, out ulong idle)
    {
        total = idle = 0;
        var parts = Split(value);
        if (parts.Length < 5 || parts[0] != "cpu")
        {
            return false;
        }

        var fields = new ulong[8];
        for (var i = 0; i < 8 && i + 1 < parts.Length; i++)
        {
            ulong.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out fields[i]);
        }

        // guest/guest_nice já estão incluídos em user/nice: ficam de fora do total.
        foreach (var f in fields)
        {
            total += f;
        }

        idle = fields[3] + fields[4];
        return total > 0;
    }

    private static void ParseMem(string value, ref long total, ref long available, ref long swapTotal, ref long swapFree)
    {
        // "MemTotal:        6037244 kB"
        var parts = Split(value);
        if (parts.Length < 2)
        {
            return;
        }

        var kb = ParseLong(parts[1]);
        switch (parts[0])
        {
            case "MemTotal:": total = kb; break;
            case "MemAvailable:": available = kb; break;
            case "SwapTotal:": swapTotal = kb; break;
            case "SwapFree:": swapFree = kb; break;
        }
    }

    // "  eth0: 1234 56 0 0 0 0 0 0 7890 12 0 0 0 0 0 0"
    private static bool TryParseNet(string value, out ulong rx, out ulong tx)
    {
        rx = tx = 0;
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var name = value[..colon].Trim();
        if (VirtualInterfacePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        var numbers = Split(value[(colon + 1)..]);
        if (numbers.Length < 9)
        {
            return false;
        }

        return ulong.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out rx) &
               ulong.TryParse(numbers[8], NumberStyles.None, CultureInfo.InvariantCulture, out tx);
    }

    // "/dev/mapper/ubuntu--vg-lv--0 68770832 46934508 18303232 72% /"
    private static DiskUsage? TryParseDisk(string value)
    {
        var parts = Split(value);
        if (parts.Length < 6 || !parts[4].EndsWith('%'))
        {
            return null;
        }

        var mount = string.Join(' ', parts.Skip(5));
        if (mount.StartsWith("/snap/", StringComparison.Ordinal) || mount.StartsWith("/var/lib/docker/", StringComparison.Ordinal))
        {
            return null;
        }

        return new DiskUsage(mount, parts[0], ParseLong(parts[1]), ParseLong(parts[2]), ParseLong(parts[3]), ParseInt(parts[4].TrimEnd('%')));
    }

    // "1234 root 12.5 3.1 dockerd"
    private static ProcessUsage? TryParseProcess(string value)
    {
        var parts = Split(value);
        if (parts.Length < 5)
        {
            return null;
        }

        return new ProcessUsage(ParseInt(parts[0]), parts[1], ParseDouble(parts[2]), ParseDouble(parts[3]), string.Join(' ', parts.Skip(4)));
    }

    private static string[] Split(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ParseInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long ParseLong(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseDouble(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
