using RootDock.Core.Monitoring;
using RootDock.Core.Sftp;

namespace RootDock.Core.Tests;

public class ServerMonitorTests
{
    // Formato real de um Ubuntu 24.04 (valores fictícios).
    private static List<string> Block(ulong cpuUser, ulong cpuIdle, ulong rx, ulong tx, double uptime, bool first) =>
    [
        .. first
            ? new[]
            {
                "@host dev-fabio",
                "@os Ubuntu 24.04.2 LTS",
                "@kernel Linux 6.8.0-136-generic",
                "@cores 4",
                "@ips 192.168.11.202 172.17.0.1",
            }
            : [],
        "@user root",
        "@users 3",
        "@usersame 2",
        $"@cpu cpu  {cpuUser} 0 1000 {cpuIdle} 500 0 50 0 0 0",
        "@load 0.58 0.41 0.35 2/230 12345",
        $"@uptime {uptime.ToString(System.Globalization.CultureInfo.InvariantCulture)} 30000.00",
        "@mem MemTotal:        6037244 kB",
        "@mem MemAvailable:    2777132 kB",
        "@mem SwapTotal:       4194300 kB",
        "@mem SwapFree:        2055204 kB",
        $"@net     lo: 999999 100 0 0 0 0 0 0 999999 100 0 0 0 0 0 0",
        $"@net   enX0: {rx} 100 0 0 0 0 0 0 {tx} 100 0 0 0 0 0 0",
        $"@net docker0: 777777 1 0 0 0 0 0 0 777777 1 0 0 0 0 0 0",
        "@df /dev/mapper/ubuntu--vg-lv--0 68770832 46934508 18303232 72% /",
        "@df /dev/xvda2 1992552 204800 1666512 11% /boot",
        "@df /dev/loop3 65536 65536 0 100% /snap/core/123",
        "@proc  1234 root 12.5  3.1 dockerd",
        "@proc   987 www-data  4.0 10.2 php-fpm8.3",
        "@cwd /srv/www",
        "@fg vim",
    ];

    [Fact]
    public void Parses_static_info_memory_disks_processes_and_terminal_folder()
    {
        var parser = new ServerSnapshotParser();

        var s = parser.Parse(Block(1000, 9000, 1_000_000, 500_000, 100.0, first: true));

        Assert.Equal("dev-fabio", s.Hostname);
        Assert.Equal("Ubuntu 24.04.2 LTS", s.OsName);
        Assert.Equal(4, s.Cores);
        Assert.Equal("root", s.User);
        Assert.Equal(2, s.UserSessions);
        Assert.Null(s.CpuPercent); // primeira leitura: sem base para calcular
        Assert.Equal(0.58, s.Load1);
        Assert.Equal(6037244, s.MemTotalKb);
        Assert.Equal(6037244 - 2777132, s.MemUsedKb);
        Assert.Equal(2, s.Disks.Count); // /snap fica de fora
        Assert.Equal("/", s.Disks[0].Mount);
        Assert.Equal(72, s.Disks[0].Percent);
        Assert.Equal("dockerd", s.TopProcesses[0].Command);
        Assert.Equal(12.5, s.TopProcesses[0].Cpu);
        Assert.Equal("/srv/www", s.TerminalDirectory);
        Assert.Equal("vim", s.TerminalForeground);
    }

    [Fact]
    public void Computes_cpu_and_network_rates_from_deltas_ignoring_virtual_interfaces()
    {
        var parser = new ServerSnapshotParser();
        parser.Parse(Block(1000, 9000, 1_000_000, 500_000, 100.0, first: true));

        // +600 jiffies ocupados, +1400 ociosos em 2 s; +250 KB recebidos e +50 KB enviados.
        var s = parser.Parse(Block(1600, 10400, 1_250_000, 550_000, 102.0, first: false));

        Assert.Equal(30.0, s.CpuPercent!.Value, 1);
        Assert.Equal(125_000, s.RxBytesPerSecond!.Value, 1);
        Assert.Equal(25_000, s.TxBytesPerSecond!.Value, 1);
        Assert.Equal("dev-fabio", s.Hostname); // informação fixa é mantida
    }

    [Fact]
    public void Script_has_no_single_quotes_or_history_bang()
    {
        Assert.DoesNotContain('\'', ServerMonitor.Script);
        Assert.DoesNotContain('!', ServerMonitor.Script);
        Assert.StartsWith("exec sh -c '", ServerMonitor.CommandText);
        Assert.DoesNotContain('\r', ServerMonitor.CommandText);
    }

    [Theory]
    [InlineData("/root", "file.txt", "/root/file.txt")]
    [InlineData("/", "etc", "/etc")]
    public void RemotePath_combines(string dir, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Combine(dir, name));

    [Theory]
    [InlineData("/srv/www/site", "/srv/www")]
    [InlineData("/srv", "/")]
    [InlineData("/", "/")]
    [InlineData("/srv/www/", "/srv")]
    public void RemotePath_parent(string path, string expected) =>
        Assert.Equal(expected, RemotePath.Parent(path));

    [Fact]
    public void RemotePath_shell_quote_escapes_single_quotes() =>
        Assert.Equal("'/tmp/it'\\''s here'", RemotePath.ShellQuote("/tmp/it's here"));
}
