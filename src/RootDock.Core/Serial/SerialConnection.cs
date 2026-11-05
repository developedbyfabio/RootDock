using System.IO.Ports;
using System.Text.Json.Serialization;
using RootDock.Core.Terminal;

namespace RootDock.Core.Serial;

[JsonConverter(typeof(JsonStringEnumConverter<SerialFlowControl>))]
public enum SerialFlowControl
{
    None,

    /// <summary>XON/XOFF (por software).</summary>
    XonXoff,

    /// <summary>RTS/CTS (por hardware).</summary>
    RtsCts,
}

/// <summary>Configuração da porta serial. Padrão dos consoles de switch/roteador: 9600 8N1, sem controle de fluxo.</summary>
public sealed record SerialSettings
{
    public static readonly int[] CommonBaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public string PortName { get; init; } = "COM1";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    /// <summary>None, Odd, Even, Mark, Space.</summary>
    public string Parity { get; init; } = "None";

    /// <summary>One, OnePointFive, Two.</summary>
    public string StopBits { get; init; } = "One";

    public SerialFlowControl FlowControl { get; init; } = SerialFlowControl.None;

    /// <summary>Resumo no formato dos manuais: "COM3 · 9600 8N1".</summary>
    public string Describe()
    {
        var parity = Parity switch { "Odd" => "O", "Even" => "E", "Mark" => "M", "Space" => "S", _ => "N" };
        var stop = StopBits switch { "Two" => "2", "OnePointFive" => "1.5", _ => "1" };
        var flow = FlowControl switch { SerialFlowControl.XonXoff => " XON/XOFF", SerialFlowControl.RtsCts => " RTS/CTS", _ => "" };
        return $"{PortName} · {BaudRate} {DataBits}{parity}{stop}{flow}";
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PortName))
        {
            return "Escolha a porta serial (ex.: COM3).";
        }

        if (BaudRate is < 50 or > 4_000_000)
        {
            return "Velocidade inválida.";
        }

        if (DataBits is < 5 or > 8)
        {
            return "Bits de dados: de 5 a 8.";
        }

        if (!Enum.TryParse<Parity>(Parity, out _) || !Enum.TryParse<StopBits>(StopBits, out var stop) || stop == System.IO.Ports.StopBits.None)
        {
            return "Paridade ou bits de parada inválidos.";
        }

        return null;
    }

    /// <summary>Portas COM existentes neste computador (ordenadas: COM2 antes de COM10).</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}

/// <summary>Console serial (COM): switches, roteadores, nobreaks e outros equipamentos.</summary>
public sealed class SerialConnection : ITerminalConnection
{
    private readonly SerialSettings _settings;
    private readonly OutboundQueue _outbound = new();
    private SerialPort? _port;
    private Thread? _reader;
    private int _closed;
    private string? _closeReason;

    public SerialConnection(SerialSettings settings)
    {
        _settings = settings;
    }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string?>? Closed;

    public bool IsConnected => _closed == 0 && _port?.IsOpen == true;

    public Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings.Validate() is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        var port = new SerialPort(_settings.PortName, _settings.BaudRate, Enum.Parse<Parity>(_settings.Parity), _settings.DataBits,
            Enum.Parse<StopBits>(_settings.StopBits))
        {
            Handshake = _settings.FlowControl switch
            {
                SerialFlowControl.XonXoff => Handshake.XOnXOff,
                SerialFlowControl.RtsCts => Handshake.RequestToSend,
                _ => Handshake.None,
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
            DtrEnable = true,
            RtsEnable = _settings.FlowControl != SerialFlowControl.RtsCts,
        };

        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            port.Dispose();
            throw new InvalidOperationException($"A porta {_settings.PortName} está em uso por outro programa (feche o outro terminal e tente de novo).");
        }
        catch (FileNotFoundException)
        {
            port.Dispose();
            throw new InvalidOperationException($"A porta {_settings.PortName} não existe neste computador. Confira o cabo/adaptador USB-serial e o Gerenciador de Dispositivos.");
        }
        catch (IOException ex)
        {
            port.Dispose();
            throw new InvalidOperationException($"Não foi possível abrir {_settings.PortName}: {ex.Message}");
        }

        _port = port;
        var stream = port.BaseStream;
        _outbound.Start(
            write: data =>
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            },
            resize: (_, _) => { }, // console serial não tem tamanho de janela
            onError: ex => Close(ex.Message));
        return Task.CompletedTask;
    }

    public void Start()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            Closed?.Invoke(_closeReason);
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"serial-read {_settings.PortName}" };
        _reader.Start();
    }

    public void Write(ReadOnlySpan<byte> data) => _outbound.EnqueueData(data);

    public void Resize(int columns, int rows)
    {
    }

    public void Dispose() => Close(null);

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        string? reason = null;
        try
        {
            var stream = _port!.BaseStream;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // Cabo/adaptador USB removido aparece aqui.
            reason = Volatile.Read(ref _closed) != 0 ? null : $"porta {_settings.PortName} fechada ({ex.Message})";
        }

        Close(reason);
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _closeReason = reason;
        _outbound.Dispose();
        var port = _port;
        // Fechar a porta pode demorar (driver USB): fora da thread de interface.
        _ = Task.Run(() =>
        {
            try
            {
                port?.Close();
            }
            catch (Exception)
            {
            }

            port?.Dispose();
        });
        Closed?.Invoke(reason);
    }
}
