using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RootDock.Core.Recording;

/// <summary>
/// Grava a saída de um terminal no formato asciicast v2 (o do asciinema): um cabeçalho JSON e
/// depois uma linha por evento [segundos, "o", texto]. Abre em qualquer player de asciinema.
/// </summary>
public sealed class AsciicastRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private bool _closed;

    public AsciicastRecorder(string path, int columns, int rows, string title)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        var header = new JsonObject
        {
            ["version"] = 2,
            ["width"] = Math.Max(1, columns),
            ["height"] = Math.Max(1, rows),
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["title"] = title,
            ["env"] = new JsonObject { ["TERM"] = "xterm-256color" },
        };
        _writer.WriteLine(header.ToJsonString());
        _writer.Flush();
    }

    public string Path { get; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public long Bytes { get; private set; }

    public void Output(string text) => Write("o", text);

    public void Resize(int columns, int rows) => Write("r", $"{columns}x{rows}");

    private void Write(string kind, string data)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var line = $"[{_clock.Elapsed.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}, \"{kind}\", {JsonSerializer.Serialize(data)}]";
            _writer.WriteLine(line);
            Bytes += line.Length;
            if (Bytes % 16384 < line.Length)
            {
                _writer.Flush();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }
}

/// <summary>Gravação lida do disco (para a lista e para reproduzir).</summary>
public sealed record Asciicast(int Width, int Height, string Title, DateTimeOffset? Started, TimeSpan Duration, IReadOnlyList<(double Time, string Kind, string Data)> Events)
{
    /// <summary>Lê o arquivo; <paramref name="headerOnly"/> pula os eventos (só duração, para a lista).</summary>
    public static Asciicast Read(string path, bool headerOnly = false)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("Arquivo vazio.");
        var header = JsonNode.Parse(headerLine) ?? throw new InvalidDataException("Cabeçalho inválido.");
        if ((int?)header["version"] != 2)
        {
            throw new InvalidDataException("Só gravações asciicast versão 2 são suportadas.");
        }

        var width = (int?)header["width"] ?? 80;
        var height = (int?)header["height"] ?? 24;
        var title = (string?)header["title"] ?? System.IO.Path.GetFileNameWithoutExtension(path);
        DateTimeOffset? started = header["timestamp"] is { } ts ? DateTimeOffset.FromUnixTimeSeconds((long)ts) : null;
        var events = new List<(double, string, string)>();
        double last = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 5)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonArray { Count: >= 3 } e)
                {
                    last = (double)e[0]!;
                    if (!headerOnly)
                    {
                        events.Add((last, (string)e[1]!, (string)e[2]!));
                    }
                }
            }
            catch (JsonException)
            {
                // última linha cortada (gravação interrompida): ignora
            }
        }

        return new Asciicast(width, height, title, started, TimeSpan.FromSeconds(last), events);
    }
}
