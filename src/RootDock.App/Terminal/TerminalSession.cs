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

    /// <summary>
    /// Teto do texto guardado enquanto a tela está em pausa (SSH). O SSH.NET não segura o servidor
    /// (renova a janela do canal assim que os dados chegam), então parar de ler só acumularia tudo
    /// na memória dele. Passando do teto, a parte mais antiga é descartada e a tela recebe um aviso.
    /// </summary>
    private const int OverflowChars = 4 * 1024 * 1024;

    /// <summary>
    /// Caracteres postados para a página e ainda não confirmados (créditos). Acima do teto o texto
    /// fica guardado aqui em vez de lotar a fila do WebView2 (que a página não enxerga). Só a thread
    /// da interface mexe neste campo.
    /// </summary>
    private const long InflightHigh = 8L * 1024 * 1024;

    /// <summary>Texto guardado acima do qual uma conexão sem perda (Telnet, serial, local) para de ler.</summary>
    private const int PendingCap = 2 * 1024 * 1024;

    private long _inflight;
    private volatile bool _paused;
    private long _droppedChars;
    private int _generation;
    private Action<ReadOnlyMemory<byte>>? _dataHandler;

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

    /// <summary>
    /// A tela pediu pausa (xterm com fila cheia). Telnet, serial e terminal local param de ler e o
    /// remetente espera (sem perda). SSH continua lendo até o teto (ver <see cref="OverflowChars"/>).
    /// </summary>
    public void PauseOutput()
    {
        lock (_gate)
        {
            _paused = true;
            if (!IsSsh)
            {
                _flow.Reset();
            }
        }
    }

    public void ResumeOutput()
    {
        lock (_gate)
        {
            _paused = false;
            _flow.Set();
        }

        _workspace.ScheduleFlush(this); // o que juntou durante a pausa
    }

    /// <summary>A página confirmou que o xterm processou <paramref name="chars"/> caracteres.</summary>
    internal void Acknowledge(long chars)
    {
        var wasBlocked = _inflight >= InflightHigh;
        _inflight = Math.Max(0, _inflight - chars);
        if (wasBlocked && _inflight < InflightHigh)
        {
            _workspace.ScheduleFlush(this);
        }
    }

    /// <summary>Página recarregada: o que estava a caminho se perdeu, recomeça do zero.</summary>
    internal void ResetFlow()
    {
        _inflight = 0;
        ResumeOutput();
    }

    /// <summary>
    /// Retira o texto acumulado para enviar à tela. Nada sai enquanto ela está em pausa ou com
    /// créditos esgotados; o texto espera aqui (SSH com teto, os demais segurando o remetente).
    /// </summary>
    internal string TakePending()
    {
        lock (_gate)
        {
            if (_pending.Length == 0 || _paused || _inflight >= InflightHigh)
            {
                return "";
            }

            var text = _pending.ToString();
            _pending.Clear();
            _inflight += text.Length;
            if (!IsSsh)
            {
                _flow.Set(); // esvaziou: a leitura pode continuar
            }
            if (_droppedChars > 0)
            {
                var megabytes = _droppedChars / (1024.0 * 1024.0);
                text = $"{Reset}\r\n{Yellow}[... {megabytes:0.#} MB de saída omitidos para não esgotar a memória ...]{Reset}\r\n" + text;
                _droppedChars = 0;
            }

            return text;
        }
    }

    /// <summary>Chamado com o lock: descarta o início do texto guardado, começando numa linha nova.</summary>
    private void TrimOverflow()
    {
        var cut = _pending.Length - OverflowChars / 2;
        var probe = _pending.ToString(cut, Math.Min(64 * 1024, _pending.Length - cut));
        var newline = probe.IndexOf('\n');
        if (newline >= 0)
        {
            cut += newline + 1; // não corta no meio de uma sequência de escape
        }

        _pending.Remove(0, cut);
        _droppedChars += cut;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitorIdle?.Stop();
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
            var generation = Interlocked.Increment(ref _generation);
            _dataHandler = data => OnData(generation, data);
            connection.DataReceived += _dataHandler;
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

    private void OnData(int generation, ReadOnlyMemory<byte> data)
    {
        // Contrapressão: se a tela pediu pausa, a thread de leitura espera aqui (exceto SSH).
        _flow.Wait();

        // Conexão antiga (reconectou enquanto esta thread esperava): o dado não é desta sessão.
        if (_disposed || generation != Volatile.Read(ref _generation))
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
                if (IsSsh)
                {
                    if (_pending.Length > OverflowChars)
                    {
                        TrimOverflow();
                    }
                }
                else if (_pending.Length > PendingCap)
                {
                    _flow.Reset(); // a próxima leitura espera a tela consumir (sem perda)
                }
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
        StartMonitor();
        if (_workspace.ActiveSession is { } active && active != this)
        {
            SetMonitorActive(false); // aberta em segundo plano (ex.: reabrir várias abas)
        }
    }

    /// <summary>
    /// Monitor de status só na aba ativa: em segundo plano ele rodaria o script no servidor a cada
    /// 2 s e atualizaria a interface sem ninguém ver. Ao sair da aba, para depois de um tempo (troca
    /// rápida entre abas não reabre o canal); ao voltar, religa na hora.
    /// </summary>
    internal void SetMonitorActive(bool active)
    {
        if (active)
        {
            _monitorIdle?.Stop();
            if (_monitor is null && State == TerminalState.Connected && _ssh?.Client is not null)
            {
                StartMonitor();
            }

            return;
        }

        if (_monitor is null)
        {
            return;
        }

        if (_monitorIdle is null)
        {
            _monitorIdle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _monitorIdle.Tick += (_, _) =>
            {
                _monitorIdle!.Stop();
                _monitor?.Dispose();
                _monitor = null;
            };
        }

        _monitorIdle.Stop();
        _monitorIdle.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _monitorIdle;

    private void StartMonitor()
    {
        if (_ssh?.Client is not { } client)
        {
            return;
        }

        var monitor = new ServerMonitor(client);
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
        Interlocked.Increment(ref _generation); // threads ainda dentro de OnData descartam o que trazem
        if (connection is not null)
        {
            if (_dataHandler is not null)
            {
                connection.DataReceived -= _dataHandler;
            }

            connection.Closed -= OnClosed;
            connection.Dispose();
        }
    }

    private void WriteStatus(string message) => WriteText($"{Dim}{message}{Reset}\r\n");

    /// <summary>Aviso em amarelo no terminal (ex.: página recarregada após falha).</summary>
    internal void WriteNotice(string message) => WriteText($"\r\n{Yellow}● {message}{Reset}\r\n");

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
