using System.Globalization;

namespace RootDock.App.Services;

/// <summary>Formatação em português (vírgula decimal) para tamanhos, taxas e durações.</summary>
public static class Format
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", PtBr) + " " + units[unit];
    }

    public static string Kb(long kb) => Bytes(kb * 1024);

    /// <summary>Memória com duas casas decimais (ex.: "3,18 GB"), como o MobaXterm exibe.</summary>
    public static string Memory(long kb)
    {
        var gb = kb / 1024.0 / 1024.0;
        return gb >= 1
            ? gb.ToString("0.00", PtBr) + " GB"
            : (kb / 1024.0).ToString("0", PtBr) + " MB";
    }

    /// <summary>Taxa de rede em bits por segundo (como provedores e o MobaXterm exibem).</summary>
    public static string BitRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is null)
        {
            return "—";
        }

        var bits = bytesPerSecond.Value * 8;
        return bits switch
        {
            >= 1e9 => (bits / 1e9).ToString("0.00", PtBr) + " Gb/s",
            >= 1e6 => (bits / 1e6).ToString("0.00", PtBr) + " Mb/s",
            >= 1e3 => (bits / 1e3).ToString("0", PtBr) + " kb/s",
            _ => bits.ToString("0", PtBr) + " b/s",
        };
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Uptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1)
        {
            var days = (int)t.TotalDays;
            return days == 1 ? "1 dia" : $"{days} dias";
        }

        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    public static string Percent(double value) => value.ToString("0", PtBr) + "%";

    public static string Number(double value, string format = "0.00") => value.ToString(format, PtBr);

    public static string Date(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return local.Year == DateTime.Now.Year ? local.ToString("dd/MM HH:mm", PtBr) : local.ToString("dd/MM/yyyy", PtBr);
    }
}
