using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RootDock.Core.History;

/// <summary>Uma transferência terminada (enviada, baixada, cancelada ou com erro).</summary>
public sealed record TransferRecord(
    [property: JsonPropertyName("t")] DateTimeOffset Time,
    [property: JsonPropertyName("up")] bool Upload,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("s")] string? Server,
    [property: JsonPropertyName("b")] long Bytes,
    [property: JsonPropertyName("r")] string Result,
    [property: JsonPropertyName("d")] double Seconds);

/// <summary>Histórico das transferências (um JSON por linha; guarda as 2000 mais recentes).</summary>
public sealed class TransferHistory
{
    public const int Max = 2000;

    private readonly string _file;
    private readonly object _gate = new();
    private List<TransferRecord>? _items;

    public TransferHistory(string file)
    {
        _file = file;
    }

    public event Action? Changed;

    public void Add(TransferRecord record)
    {
        lock (_gate)
        {
            Load();
            _items!.Add(record);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                if (_items.Count > Max + 200)
                {
                    _items.RemoveRange(0, _items.Count - Max);
                    File.WriteAllLines(_file, _items.Select(i => JsonSerializer.Serialize(i)), new UTF8Encoding(false));
                }
                else
                {
                    File.AppendAllText(_file, JsonSerializer.Serialize(record) + "\n", new UTF8Encoding(false));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Mais recentes primeiro.</summary>
    public IReadOnlyList<TransferRecord> Recent(int limit = 500)
    {
        lock (_gate)
        {
            Load();
            return _items!.AsEnumerable().Reverse().Take(limit).ToList();
        }
    }

    private void Load()
    {
        if (_items is not null)
        {
            return;
        }

        _items = new List<TransferRecord>();
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_file))
            {
                try
                {
                    if (line.Length > 0 && JsonSerializer.Deserialize<TransferRecord>(line) is { } record)
                    {
                        _items.Add(record);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }
}
