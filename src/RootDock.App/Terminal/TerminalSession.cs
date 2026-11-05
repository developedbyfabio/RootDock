using System.Buffers;
using System.Text;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.Core.Monitoring;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;
using RootDock.Core.Ssh;
using RootDock.Core.Terminal;

namespace RootDock.App.Terminal;

public enum TerminalState
{
    Connecting,
    Connected,
    Closed,
}

/// <summary>
/// Um painel de terminal: conecta, decodifica a saída (UTF-8 com estado entre pacotes),
/// acumula para envio em lote à tela e aplica contrapressão quando a tela não acompanha.
/// </summary>
public sealed class TerminalSession : IDisposable, ITerminalPrompter
{
    private const string Dim = "\u001b[90m";
    private const string Yellow = "\u001b[33m";
    private const string Reset = "\u001b[0m";

    private readonly TerminalWorkspace _workspace;
    private readonly SessionConnector _connector;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _flow = new(true);

    private ITerminalConnection? _connection;
    private SshTerminalConnection? _ssh;
    private ServerMonitor? _monitor;
    private CancellationTokenSource? _connectCts;
    private int _columns = 80;
    private int _rows = 24;
    private bool _started;
    private volatile bool _disposed;

    public TerminalSession(string id, SessionEntry entry, TerminalWorkspace workspace, SessionConnector connector)
    {
        Id = id;
        Entry = entry;
        _workspace = workspace;
        _connector = connector;
    }

    public string Id { get; }

    public SessionEntry Entry { get; }

    public TerminalState State { get; private set; } = TerminalState.Connecting;

    /// <summary>Estado do servidor (só SSH). Criado na primeira conexão.</summary>
    public ServerStatusViewModel? ServerStatus { get; private set; }

    /// <summary>Arquivos do servidor (só SSH). O SFTP abre quando o painel é exibido.</summary>
    public RemoteFilesViewModel? Files { get; private set; }

    public bool IsSsh => Entry.Kind == SessionKind.Ssh;

    /// <summary>Cliente SSH conectado deste terminal (para comandos à parte, como instalar a chave).</summary>
    public Renci.SshNet.SshClient? SshClient => _ssh?.Client;

    /// <summary>Credenciais que entraram no servidor (para a divisão de tela não perguntar de novo).</summary>
    public SshCredentials? Credentials => _ssh?.Credentials;

    /// <summary>Login herdado do painel de origem numa divisão de tela.</summary>
    public SshCredentials? ReuseCredentials { get; set; }

    /// <summary>Bastions herdados do painel de origem (mesmo caminho, sem perguntar de novo).</summary>
    public IReadOnlyList<SshHop>? ReuseJumps { get; set; }

    public IReadOnlyList<SshHop>? Jumps => _ssh?.Jumps;

    /// <summary>Endereço real da conexão (túnel do bastion, quando houver).</summary>
    public string? ConnectHost => _ssh?.ConnectHost;

    public int? ConnectPort => _ssh?.ConnectPort;

    /// <summary>Usuário que de fato entrou no servidor (pode ter sido digitado no terminal).</summary>
    public string? SshUsername => _ssh?.Credentials.Username;

    public bool IsDisposed => _disposed;

    /// <summary>Programa em primeiro plano no terminal, informado pelo monitor (null = desconhecido).</summary>
    public string? ForegroundProcess { get; private set; }

    /// <summary>Tamanho informado pela tela. A primeira medida dispara a conexão.</summary>
    /// <summary>Gravação em andamento (asciicast), ou null.</summary>
    public Core.Recording.AsciicastRecorder? Recorder { get; private set; }

    /// <summary>Começa a gravar tudo o que aparece neste terminal.</summary>
    public string StartRecording(string directory)
    {
        StopRecording();
        var safe = string.Concat(Entry.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = System.IO.Path.Combine(directory, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.cast");
        for (var i = 2; System.IO.File.Exists(path); i++)
        {
            path = System.IO.Path.Combine(directory, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({i}).cast");
        }
        Recorder = new Core.Recording.AsciicastRecorder(path, _columns, _rows, Entry.Name);
        return path;
    }

    /// <summary>Para a gravação; retorna o arquivo salvo.</summary>
    public string? StopRecording()
    {
        var recorder = Recorder;
        Recorder = null;
        recorder?.Dispose();
        return recorder?.Path;
    }

    public void OnResize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        if (columns != _columns || rows != _rows)
        {
            Recorder?.Resize(columns, rows);
        }

        _columns = columns;
        _rows = rows;
        if (!_started)
        {
            _started = true;
            _ = ConnectAsync();
        }
        else
        {
            _connection?.Resize(columns, rows);
        }
    }

    public void Reconnect()
    {
        if (State != TerminalState.Closed || _disposed)
        {
            return;
        }

        DetachConnection();
        _decoder.Reset();
        WriteText("\r\n");
        _ = ConnectAsync();
    }

    public void SendInput(string text)
    {
        if (State == TerminalState.Connected)
        {
            _connection?.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    // ITerminalPrompter: perguntas de login respondidas no próprio terminal.
    Task<string?> ITerminalPrompter.AskAsync(string prompt, bool echo, CancellationToken ct)
    {
        WriteText(prompt);
        return _workspace.PromptAsync(this, echo, ct);
    }

    void ITerminalPrompter.Write(string text) => WriteText(text);

    Task<string?> ITerminalPrompter.OfferAsync(string text, IReadOnlyList<OfferAction> actions) =>
        _workspace.OfferAsync(this, text, actions);

    /// <summary>Digita pelo terminal visual (o comando fica destacado no histórico).</summary>
    public void Type(string text) => _workspace.TypeInto(this, text);

    /// <summary>Dados binários do xterm (relatórios de mouse antigos): cada caractere é um byte.</summary>
    public void SendBinary(string data)
    {
        if (State == TerminalState.Connected)
        {
            _connection?.Write(Encoding.Latin1.GetBytes(data));
        }
    }

    public void PauseOutput() => _flow.Reset();

    public void ResumeOutput() => _flow.Set();

    /// <summary>Retira o texto acumulado para enviar à tela.</summary>
    internal string TakePending()
    {
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return "";
            }

            var text = _pending.ToString();
            _pending.Clear();
            return text;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRecording();
        _workspace.Services?.Tunnels.Detach(this);
        _connectCts?.Cancel();
        _flow.Set();
        DetachConnection();
        if (Files is not null)
        {
            _workspace.Services?.Editor.Forget(Files);
            _workspace.Editors?.Forget(Files);
            Files.Dispose();
        }
    }

    private async Task ConnectAsync()
    {
        SetState(TerminalState.Connecting);
        if (Entry.Kind == SessionKind.LocalShell)
        {
            // No SSH quem escreve é o conector (depois de pedir usuário e senha no terminal).
            WriteStatus($"Abrindo {Entry.Name}...");
        }

        _connectCts = new CancellationTokenSource();
        try
        {
            var connection = await _connector.ConnectAsync(Entry, _columns, _rows, this, _connectCts.Token, ReuseCredentials, ReuseJumps);
            if (_disposed)
            {
                connection.Dispose();
                return;
            }

            _connection = connection;
            connection.DataReceived += OnData;
            connection.Closed += OnClosed;
            if (connection is SshTerminalConnection ssh)
            {
                StartRemoteFeatures(ssh);
            }

            SetState(TerminalState.Connected);
            connection.Start();

            if (!string.IsNullOrWhiteSpace(Entry.StartupCommand))
            {
                connection.Write(Encoding.UTF8.GetBytes(Entry.StartupCommand + "\r"));
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                Log.Error($"Conectar {Entry.Endpoint}", ex);
            }

            if (!_disposed)
            {
                WriteText($"{Yellow}● {SessionConnector.Describe(ex)}{Reset}\r\n{Dim}Pressione Enter para tentar de novo.{Reset}\r\n");
                SetState(TerminalState.Closed);
            }
        }
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        // Contrapressão: se a tela pediu pausa, a thread de leitura espera aqui.
        _flow.Wait();
        if (_disposed)
        {
            return;
        }

        var bytes = data.Span;
        var chars = ArrayPool<char>.Shared.Rent(bytes.Length + 4);
        try
        {
            var count = _decoder.GetChars(bytes, chars, flush: false);
            lock (_gate)
            {
                _pending.Append(chars, 0, count);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        _workspace.ScheduleFlush(this);
    }

    private void OnClosed(string? reason)
    {
        if (_disposed)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}";
        WriteText($"\r\n{Yellow}● Sessão encerrada{detail}{Reset}\r\n{Dim}Pressione Enter para reconectar.{Reset}\r\n");
        _workspace.Dispatcher.BeginInvoke(StopRemoteFeatures);
        SetState(TerminalState.Closed);
    }

    private void StartRemoteFeatures(SshTerminalConnection ssh)
    {
        _ssh = ssh;
        var services = _workspace.Services;
        ServerStatus ??= new ServerStatusViewModel();
        if (Files is null && services is not null)
        {
            Files = new RemoteFilesViewModel(Entry, ConnectSftpAsync, Type, services.Transfers);
            Files.Notice += services.Notify;
        }
        else
        {
            Files?.SessionAvailable();
        }

        if (ssh.Client is null)
        {
            return;
        }

        services?.Tunnels.Attach(this);
        var monitor = new ServerMonitor(ssh.Client);
        monitor.SnapshotReceived += snapshot => _workspace.Dispatcher.BeginInvoke(() =>
        {
            if (_monitor != monitor)
            {
                return;
            }

            ServerStatus?.Update(snapshot);
            Files?.OnTerminalDirectory(snapshot.TerminalDirectory);
            ForegroundProcess = snapshot.TerminalForeground;
        });
        monitor.Stopped += reason => _workspace.Dispatcher.BeginInvoke(() =>
        {
            if (_monitor == monitor)
            {
                ServerStatus?.SetStopped(reason);
            }
        });
        _monitor = monitor;
        monitor.Start();
    }

    private void StopRemoteFeatures()
    {
        _workspace.Services?.Tunnels.Detach(this);
        _monitor?.Dispose();
        _monitor = null;
        _ssh = null;
        ServerStatus?.SetStopped("sessão desconectada");
        Files?.Disconnect("Sessão desconectada. Pressione Enter no terminal para reconectar.");
    }

    private Task<RemoteFileSystem> ConnectSftpAsync(CancellationToken ct)
    {
        var ssh = _ssh ?? throw new InvalidOperationException("Sessão desconectada.");
        // Fora da thread de interface: a verificação da chave do servidor pode abrir um diálogo.
        // Com bastion, o SFTP passa pelo mesmo túnel do terminal (a chave é verificada pelo nome real).
        return Task.Run(() => RemoteFileSystem.ConnectAsync(
            ssh.Host, ssh.Port, ssh.Credentials, ssh.KnownHosts, ssh.Interaction, ct, ssh.ConnectHost, ssh.ConnectPort), ct);
    }

    private void DetachConnection()
    {
        _monitor?.Dispose();
        _monitor = null;
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            connection.DataReceived -= OnData;
            connection.Closed -= OnClosed;
            connection.Dispose();
        }
    }

    private void WriteStatus(string message) => WriteText($"{Dim}{message}{Reset}\r\n");

    private void WriteText(string text)
    {
        lock (_gate)
        {
            _pending.Append(text);
        }

        _workspace.ScheduleFlush(this);
    }

    private void SetState(TerminalState state)
    {
        State = state;
        _workspace.NotifyState(this);
    }
}
