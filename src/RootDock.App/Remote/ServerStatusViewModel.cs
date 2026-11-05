using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RootDock.App.Services;
using RootDock.Core.Monitoring;

namespace RootDock.App.Remote;

public sealed record DiskRow(string Mount, string Text, double Percent, Brush Brush, string ToolTip);

public sealed record ProcessRow(string Command, string User, string Cpu, string Memory, int Pid);

/// <summary>Estado do servidor de um terminal SSH, pronto para a barra de status.</summary>
public sealed partial class ServerStatusViewModel : ObservableObject
{
    public const int HistoryLength = 60;
    private const double SparkWidth = 64;
    private const double SparkHeight = 16;

    public static readonly Brush Ok = Frozen(0x9E, 0xCE, 0x6A);
    public static readonly Brush Warn = Frozen(0xE0, 0xAF, 0x68);
    public static readonly Brush Danger = Frozen(0xF7, 0x76, 0x8E);
    public static readonly Brush Accent = Frozen(0x7A, 0xA2, 0xF7);
    public static readonly Brush Muted = Frozen(0x8B, 0x93, 0xA5);

    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _rx = new();
    private readonly Queue<double> _tx = new();

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _message = "Coletando dados do servidor...";
    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private string _systemText = "";
    [ObservableProperty] private string _userText = "";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private Brush _cpuBrush = Ok;
    [ObservableProperty] private PointCollection _cpuPoints = new();
    [ObservableProperty] private string _memText = "—";
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private Brush _memBrush = Ok;
    [ObservableProperty] private string _swapText = "";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private PointCollection _rxPoints = new();
    [ObservableProperty] private PointCollection _txPoints = new();
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _loadText = "";
    [ObservableProperty] private string _ipText = "";
    [ObservableProperty] private IReadOnlyList<DiskRow> _mainDisks = [];
    [ObservableProperty] private IReadOnlyList<DiskRow> _allDisks = [];
    [ObservableProperty] private IReadOnlyList<ProcessRow> _processes = [];
    [ObservableProperty] private string _updatedText = "";

    public void Update(ServerSnapshot s)
    {
        HasData = true;
        Hostname = s.Hostname ?? "servidor";
        SystemText = string.Join("  ·  ", new[] { s.OsName, s.Kernel, s.Cores > 0 ? $"{s.Cores} núcleos" : null }.Where(x => !string.IsNullOrEmpty(x)));
        IpText = s.IpAddresses ?? "";
        UserText = s.User is null ? "" : s.UserSessions > 1 ? $"{s.User} (x{s.UserSessions})" : s.User;

        if (s.CpuPercent is { } cpu)
        {
            Push(_cpu, cpu);
            CpuText = Format.Percent(cpu);
            CpuBrush = Level(cpu, 60, 85);
            CpuPoints = Spark(_cpu, 100);
        }

        LoadText = $"Carga: {Format.Number(s.Load1)}  {Format.Number(s.Load5)}  {Format.Number(s.Load15)}";

        if (s.MemTotalKb > 0)
        {
            MemPercent = s.MemPercent;
            MemText = $"{Format.Memory(s.MemUsedKb)} / {Format.Memory(s.MemTotalKb)}";
            MemBrush = Level(s.MemPercent, 75, 90);
            SwapText = s.SwapTotalKb > 0 ? $"Swap: {Format.Memory(s.SwapUsedKb)} / {Format.Memory(s.SwapTotalKb)}" : "Sem swap";
        }

        if (s.RxBytesPerSecond is { } rx && s.TxBytesPerSecond is { } tx)
        {
            Push(_rx, rx);
            Push(_tx, tx);
            DownText = Format.BitRate(rx);
            UpText = Format.BitRate(tx);
            var max = Math.Max(_rx.DefaultIfEmpty().Max(), _tx.DefaultIfEmpty().Max());
            RxPoints = Spark(_rx, max);
            TxPoints = Spark(_tx, max);
        }

        UptimeText = s.UptimeSeconds > 0 ? Format.Uptime(s.UptimeSeconds) : "—";

        var disks = s.Disks
            .Select(d => new DiskRow(
                d.Mount,
                $"{d.Mount}: {d.Percent}%",
                d.Percent,
                Level(d.Percent, 80, 90),
                $"{d.Mount}  ({d.FileSystem})\n{Format.Kb(d.UsedKb)} usados de {Format.Kb(d.SizeKb)}  ·  {Format.Kb(d.AvailableKb)} livres"))
            .ToList();
        AllDisks = disks;
        // Barra: sempre "/" e, depois, os mais cheios.
        MainDisks = disks.OrderByDescending(d => d.Mount == "/").ThenByDescending(d => d.Percent).Take(2).ToList();

        Processes = s.TopProcesses
            .Select(p => new ProcessRow(p.Command, p.User, Format.Percent(p.Cpu), Format.Number(p.Memory, "0.0") + "%", p.Pid))
            .ToList();
        UpdatedText = $"Atualizado às {DateTime.Now:HH:mm:ss}";
    }

    public void SetStopped(string reason)
    {
        Message = reason;
        if (!HasData)
        {
            return;
        }

        UpdatedText = $"Monitor parado: {reason}";
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistoryLength)
        {
            queue.Dequeue();
        }
    }

    private static PointCollection Spark(IEnumerable<double> values, double max)
    {
        var list = values.ToList();
        var points = new PointCollection(list.Count + 2);
        if (list.Count == 0)
        {
            points.Freeze();
            return points;
        }

        var scale = max <= 0 ? 0 : (SparkHeight - 1) / max;
        var step = SparkWidth / (HistoryLength - 1);
        var x0 = SparkWidth - (list.Count - 1) * step;
        points.Add(new Point(x0, SparkHeight));
        for (var i = 0; i < list.Count; i++)
        {
            points.Add(new Point(x0 + i * step, SparkHeight - Math.Min(list[i] * scale, SparkHeight - 1)));
        }

        points.Add(new Point(SparkWidth, SparkHeight));
        points.Freeze();
        return points;
    }

    private static Brush Level(double value, double warn, double danger) =>
        value >= danger ? Danger : value >= warn ? Warn : Ok;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
