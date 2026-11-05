using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RootDock.Core.Terminal;

namespace RootDock.Core.Telnet;

/// <summary>Usuário e senha digitados sozinhos quando o equipamento pede ("login:", "Password:").</summary>
public sealed record TelnetAutoLogin(string Username, string? Password);

/// <summary>
/// Terminal Telnet (RFC 854) para equipamentos e sistemas legados. Negocia: tamanho da janela
/// (NAWS, RFC 1073), tipo de terminal (RFC 1091, "XTERM-256COLOR"), eco e supressão de go-ahead
/// do servidor e modo binário (acentos em UTF-8). Recusa as demais opções.
/// O tráfego Telnet não é criptografado: use só em redes confiáveis.
/// </summary>
public sealed partial class TelnetConnection : ITerminalConnection
{
    internal const byte Iac = 255;
    internal const byte Dont = 254;
    internal const byte Do = 253;
    internal const byte Wont = 252;
    internal const byte Will = 251;
    internal const byte Sb = 250;
    internal const byte Se = 240;

    internal const byte OptBinary = 0;
    internal const byte OptEcho = 1;
    internal const byte OptSga = 3;
    internal const byte OptTtype = 24;
    internal const byte OptNaws = 31;

    private const string TerminalType = "XTERM-256COLOR";
    private static readonly TimeSpan AutoLoginWindow = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _port;
    private readonly TelnetAutoLogin? _autoLogin;
    private readonly OutboundQueue _outbound = new();
    private readonly object _negotiationGate = new();

    // Negociação (thread de leitura) e digitação (fila de saída) escrevem no mesmo socket.
    private readonly object _writeGate = new();

    // Opções aceitas: as nossas (WILL) e as do servidor (DO).
    private readonly HashSet<byte> _localEnabled = [];
    private readonly HashSet<byte> _remoteEnabled = [];

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _reader;
    private int _closed;
    private string? _closeReason;
    private int _columns;
    private int _rows;

    // Estado do parser entre pacotes.
    private ParseState _state;
    private byte _verb;
    private readonly List<byte> _subnegotiation = [];
    private bool _lastWasCr;

    // Login automático.
    private readonly StringBuilder _recent = new();
    private DateTime _autoLoginUntil;
    private bool _userSent;
    private bool _passwordSent;

    public TelnetConnection(string host, int port, TelnetAutoLogin? autoLogin = null)
    {
        _host = host;
        _port = port;
        _autoLogin = autoLogin;
    }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string?>? Closed;

    public bool IsConnected => _closed == 0 && _client?.Connected == true;

    private enum ParseState
    {
        Data,
        Iac,
        Verb,
        Sub,
        SubIac,
    }

    public async Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        _columns = columns;
        _rows = rows;
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Tempo esgotado: {_host}:{_port} não respondeu.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _autoLoginUntil = DateTime.UtcNow + AutoLoginWindow;
        _outbound.Start(
            write: data =>
            {
                lock (_writeGate)
                {
                    _stream.Write(data, 0, data.Length);
                }
            },
            resize: (c, r) => SendRaw(WindowSize(c, r)),
            onError: ex => Close(ex.Message));

        // Como o PuTTY: oferece tamanho da janela e tipo de terminal e pede eco/SGA do servidor.
        lock (_negotiationGate)
        {
            _localEnabled.Add(OptNaws);
            _localEnabled.Add(OptTtype);
            _localEnabled.Add(OptSga);
            _remoteEnabled.Add(OptSga);
            _remoteEnabled.Add(OptEcho);
        }

        SendRaw([Iac, Will, OptNaws, Iac, Will, OptTtype, Iac, Will, OptSga, Iac, Do, OptSga, Iac, Do, OptEcho]);
    }

    public void Start()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            Closed?.Invoke(_closeReason);
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"telnet-read {_host}" };
        _reader.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var binary = IsLocalBinary;
        var output = new List<byte>(data.Length + 8);
        foreach (var b in data)
        {
            if (b == Iac)
            {
                output.Add(Iac);
                output.Add(Iac);
            }
            else if (b == (byte)'\r' && !binary)
            {
                // Enter no modo NVT: CR LF ("Telnet New Line"), como o PuTTY.
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }
            else
            {
                output.Add(b);
            }
        }

        _outbound.EnqueueData(output.ToArray());
    }

    public void Resize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        bool naws;
        lock (_negotiationGate)
        {
            naws = _localEnabled.Contains(OptNaws);
        }

        if (naws && columns > 0 && rows > 0)
        {
            _outbound.EnqueueResize(columns, rows);
        }
    }

    public void Dispose() => Close(null);

    private bool IsLocalBinary
    {
        get
        {
            lock (_negotiationGate)
            {
                return _localEnabled.Contains(OptBinary);
            }
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        string? reason = null;
        try
        {
            while (true)
            {
                var read = _stream!.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var data = Process(buffer.AsSpan(0, read));
                if (data.Length > 0)
                {
                    WatchAutoLogin(data);
                    DataReceived?.Invoke(data);
                }
            }
        }
        catch (Exception ex)
        {
            reason = Volatile.Read(ref _closed) != 0 ? null : ex.Message;
        }

        Close(reason);
    }

    /// <summary>Separa os comandos Telnet dos dados. Mantém o estado entre pacotes.</summary>
    internal byte[] Process(ReadOnlySpan<byte> input)
    {
        var data = new List<byte>(input.Length);
        foreach (var b in input)
        {
            switch (_state)
            {
                case ParseState.Data:
                    if (b == Iac)
                    {
                        _state = ParseState.Iac;
                    }
                    else if (_lastWasCr && b == 0 && !IsRemoteBinary())
                    {
                        // CR NUL = CR no modo NVT.
                        _lastWasCr = false;
                    }
                    else
                    {
                        data.Add(b);
                        _lastWasCr = b == (byte)'\r';
                    }

                    break;
                case ParseState.Iac:
                    switch (b)
                    {
                        case Iac:
                            data.Add(Iac);
                            _state = ParseState.Data;
                            break;
                        case Do or Dont or Will or Wont:
                            _verb = b;
                            _state = ParseState.Verb;
                            break;
                        case Sb:
                            _subnegotiation.Clear();
                            _state = ParseState.Sub;
                            break;
                        default:
                            _state = ParseState.Data; // NOP, GA, AYT...: ignorados
                            break;
                    }

                    break;
                case ParseState.Verb:
                    Negotiate(_verb, b);
                    _state = ParseState.Data;
                    break;
                case ParseState.Sub:
                    if (b == Iac)
                    {
                        _state = ParseState.SubIac;
                    }
                    else
                    {
                        _subnegotiation.Add(b);
                    }

                    break;
                case ParseState.SubIac:
                    if (b == Se)
                    {
                        Subnegotiate();
                        _state = ParseState.Data;
                    }
                    else
                    {
                        _subnegotiation.Add(b); // IAC IAC dentro da subnegociação
                        _state = ParseState.Sub;
                    }

                    break;
            }
        }

        return data.ToArray();
    }

    private bool IsRemoteBinary()
    {
        lock (_negotiationGate)
        {
            return _remoteEnabled.Contains(OptBinary);
        }
    }

    private void Negotiate(byte verb, byte option)
    {
        byte[]? reply = null;
        var sendSize = false;
        lock (_negotiationGate)
        {
            switch (verb)
            {
                case Do:
                    if (option is OptNaws or OptTtype or OptSga or OptBinary)
                    {
                        if (_localEnabled.Add(option))
                        {
                            reply = [Iac, Will, option];
                        }

                        sendSize = option == OptNaws;
                    }
                    else
                    {
                        reply = [Iac, Wont, option];
                    }

                    break;
                case Dont:
                    if (_localEnabled.Remove(option))
                    {
                        reply = [Iac, Wont, option];
                    }

                    break;
                case Will:
                    if (option is OptEcho or OptSga or OptBinary)
                    {
                        if (_remoteEnabled.Add(option))
                        {
                            reply = [Iac, Do, option];
                        }
                    }
                    else
                    {
                        reply = [Iac, Dont, option];
                    }

                    break;
                case Wont:
                    if (_remoteEnabled.Remove(option))
                    {
                        reply = [Iac, Dont, option];
                    }

                    break;
            }
        }

        if (reply is not null)
        {
            SendRaw(reply);
        }

        if (sendSize)
        {
            SendRaw(WindowSize(_columns, _rows));
        }
    }

    private void Subnegotiate()
    {
        // Tipo de terminal: o servidor manda SB TTYPE SEND (1); respondemos SB TTYPE IS (0) "XTERM-256COLOR".
        if (_subnegotiation.Count >= 2 && _subnegotiation[0] == OptTtype && _subnegotiation[1] == 1)
        {
            var reply = new List<byte> { Iac, Sb, OptTtype, 0 };
            reply.AddRange(Encoding.ASCII.GetBytes(TerminalType));
            reply.AddRange([Iac, Se]);
            SendRaw(reply.ToArray());
        }
    }

    /// <summary>SB NAWS: largura e altura em 16 bits (255 duplicado, como manda o protocolo).</summary>
    internal static byte[] WindowSize(int columns, int rows)
    {
        var bytes = new List<byte> { Iac, Sb, OptNaws };
        foreach (var value in new[] { (ushort)Math.Clamp(columns, 1, 65535), (ushort)Math.Clamp(rows, 1, 65535) })
        {
            foreach (var part in new[] { (byte)(value >> 8), (byte)value })
            {
                bytes.Add(part);
                if (part == Iac)
                {
                    bytes.Add(Iac);
                }
            }
        }

        bytes.AddRange([Iac, Se]);
        return bytes.ToArray();
    }

    private void SendRaw(byte[] bytes)
    {
        try
        {
            lock (_writeGate)
            {
                _stream?.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(ex.Message);
        }
    }

    [GeneratedRegex(@"(?:login|username|user name|usuario|usuário)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UserPrompt();

    [GeneratedRegex(@"(?:password|senha|passcode)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();

    /// <summary>Digita usuário e senha quando o equipamento pede (só nos primeiros 30 s e uma vez cada).</summary>
    private void WatchAutoLogin(byte[] data)
    {
        if (_autoLogin is null || (_userSent && (_passwordSent || _autoLogin.Password is null)) || DateTime.UtcNow > _autoLoginUntil)
        {
            return;
        }

        _recent.Append(Encoding.Latin1.GetString(data));
        if (_recent.Length > 400)
        {
            _recent.Remove(0, _recent.Length - 400);
        }

        var tail = _recent.ToString().TrimEnd(' ');
        if (!_userSent && UserPrompt().IsMatch(tail))
        {
            _userSent = true;
            _recent.Clear();
            Write(Encoding.UTF8.GetBytes(_autoLogin.Username + "\r"));
        }
        else if (!_passwordSent && _autoLogin.Password is { } password && PasswordPrompt().IsMatch(tail))
        {
            _passwordSent = true;
            _recent.Clear();
            Write(Encoding.UTF8.GetBytes(password + "\r"));
        }
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _closeReason = reason;
        _outbound.Dispose();
        try
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch (Exception)
        {
            // Encerramento.
        }

        Closed?.Invoke(reason);
    }
}
