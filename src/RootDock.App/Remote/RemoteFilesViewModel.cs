using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Renci.SshNet.Common;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;
using Wpf.Ui.Controls;
using Visibility = System.Windows.Visibility;

namespace RootDock.App.Remote;

public enum FilesState
{
    Idle,
    Connecting,
    Ready,
    Error,
}

public sealed class RemoteEntryViewModel
{
    private static readonly HashSet<string> ArchiveExt = [".zip", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".7z", ".rar", ".zst"];
    private static readonly HashSet<string> ImageExt = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp", ".ico"];
    private static readonly HashSet<string> CodeExt = [".php", ".js", ".ts", ".py", ".sh", ".cs", ".java", ".go", ".rb", ".css", ".html", ".vue", ".sql"];
    private static readonly HashSet<string> ConfigExt = [".conf", ".cfg", ".ini", ".yml", ".yaml", ".json", ".xml", ".env", ".toml", ".service"];

    public RemoteEntryViewModel(RemoteEntry entry)
    {
        Entry = entry;
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        (Icon, IconBrush) = entry switch
        {
            { IsDirectory: true, IsSymlink: true } => (SymbolRegular.FolderLink20, ServerStatusViewModel.Warn),
            { IsDirectory: true } => (SymbolRegular.Folder20, ServerStatusViewModel.Warn),
            { IsSymlink: true } => (SymbolRegular.Link20, ServerStatusViewModel.Accent),
            _ when ArchiveExt.Contains(ext) => (SymbolRegular.FolderZip20, ServerStatusViewModel.Danger),
            _ when ImageExt.Contains(ext) => (SymbolRegular.Image20, ServerStatusViewModel.Ok),
            _ when CodeExt.Contains(ext) => (SymbolRegular.Code20, ServerStatusViewModel.Accent),
            _ when ConfigExt.Contains(ext) || entry.Name.StartsWith(".", StringComparison.Ordinal) => (SymbolRegular.DocumentSettings20, ServerStatusViewModel.Muted),
            _ when ext is ".log" or ".txt" or ".md" => (SymbolRegular.DocumentText20, ServerStatusViewModel.Muted),
            _ => (SymbolRegular.Document20, ServerStatusViewModel.Muted),
        };
        SizeText = entry.IsDirectory ? "" : Format.Bytes(entry.Size);
        ToolTip = $"{entry.FullPath}\n{entry.Permissions}   uid {entry.UserId}  gid {entry.GroupId}\n" +
                  (entry.IsDirectory ? "" : $"{Format.Bytes(entry.Size)}  ·  ") + $"modificado {Format.Date(entry.Modified)}";
    }

    public RemoteEntry Entry { get; }

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public SymbolRegular Icon { get; }

    public Brush IconBrush { get; }

    /// <summary>Ícone colorido do tipo de arquivo/pasta, como no VS Code (null se os ícones não carregaram).</summary>
    public ImageSource? IconImage => FileIconTheme.For(Entry.Name, Entry.IsDirectory);

    /// <summary>Sem o ícone colorido, mostra o símbolo simples.</summary>
    public Visibility GlyphVisibility => IconImage is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Marca de link simbólico no canto do ícone.</summary>
    public Visibility LinkVisibility => Entry.IsSymlink && IconImage is not null ? Visibility.Visible : Visibility.Collapsed;

    public string SizeText { get; }

    public string ToolTip { get; }

    public double Opacity => Entry.IsHidden ? 0.65 : 1;
}

/// <summary>
/// Painel de arquivos de um terminal SSH: SFTP aberto sob demanda, navegação, filtro,
/// acompanhamento da pasta do terminal e operações (enviar, baixar, renomear, excluir).
/// </summary>
public sealed partial class RemoteFilesViewModel : ObservableObject, IDisposable
{
    private readonly Func<CancellationToken, Task<RemoteFileSystem>> _connect;
    private readonly Action<string> _sendToTerminal;
    private readonly TransferQueue _transfers;
    private RemoteFileSystem? _fs;
    private Task? _connecting;
    private CancellationTokenSource? _listCts;
    private List<RemoteEntry> _all = [];
    private string? _terminalDirectory;
    private bool _disposed;

    public RemoteFilesViewModel(
        SessionEntry session,
        Func<CancellationToken, Task<RemoteFileSystem>> connect,
        Action<string> sendToTerminal,
        TransferQueue transfers)
    {
        Session = session;
        _connect = connect;
        _sendToTerminal = sendToTerminal;
        _transfers = transfers;
    }

    public SessionEntry Session { get; }

    public BulkObservableCollection<RemoteEntryViewModel> Entries { get; } = [];

    /// <summary>Linhas já montadas da pasta atual (filtrar reaproveita em vez de recriar).</summary>
    private readonly Dictionary<RemoteEntry, RemoteEntryViewModel> _rows = new(ReferenceEqualityComparer.Instance);

    [ObservableProperty] private FilesState _state = FilesState.Idle;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathInput = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _showHidden = true;
    [ObservableProperty] private bool _followTerminal = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    public bool IsProduction => Session.Environment == SessionEnvironment.Production;

    /// <summary>Mensagens curtas para a barra de status (texto, é erro?).</summary>
    public event Action<string, bool>? Notice;

    public bool IsReady => State == FilesState.Ready;

    partial void OnStateChanged(FilesState value) => OnPropertyChanged(nameof(IsReady));

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    partial void OnFollowTerminalChanged(bool value)
    {
        if (value && _terminalDirectory is not null && _terminalDirectory != CurrentPath && _fs is not null)
        {
            _ = NavigateAsync(_terminalDirectory);
        }
    }

    public async Task EnsureConnectedAsync()
    {
        if (_disposed || IsSessionDisconnected || _fs is { IsConnected: true })
        {
            return;
        }

        _connecting ??= ConnectCoreAsync();
        try
        {
            await _connecting;
        }
        finally
        {
            _connecting = null;
        }
    }

    /// <summary>SFTP conectado (reabre se caiu). Usado também pela edição remota.</summary>
    public async Task<RemoteFileSystem> GetFileSystemAsync()
    {
        await EnsureConnectedAsync();
        return _fs ?? throw new IOException(Message.Length > 0 ? Message : "SFTP indisponível.");
    }

    /// <summary>Terminal desconectado: o SFTP só reabre quando a sessão voltar.</summary>
    public bool IsSessionDisconnected { get; private set; }

    /// <summary>A sessão de terminal caiu: fecha o SFTP (será reaberto ao reconectar).</summary>
    public void Disconnect(string reason)
    {
        _listCts?.Cancel();
        _fs?.Dispose();
        _fs = null;
        IsSessionDisconnected = true;
        State = FilesState.Idle;
        Message = reason;
    }

    /// <summary>O terminal reconectou: o painel pode abrir o SFTP de novo.</summary>
    public void SessionAvailable()
    {
        IsSessionDisconnected = false;
        if (State == FilesState.Idle)
        {
            Message = "";
        }
    }

    public void OnTerminalDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || directory == _terminalDirectory)
        {
            return;
        }

        _terminalDirectory = directory;
        if (!FollowTerminal)
        {
            return;
        }

        if (_fs is null)
        {
            // Ainda não aberto: começa nesta pasta quando o painel for exibido.
            CurrentPath = directory;
            return;
        }

        if (directory != CurrentPath)
        {
            _ = NavigateAsync(directory);
        }
    }

    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = path.Trim();
        if (_fs is null)
        {
            CurrentPath = path;
            await EnsureConnectedAsync();
            return;
        }

        _listCts?.Cancel();
        var cts = new CancellationTokenSource();
        _listCts = cts;
        IsBusy = true;
        try
        {
            var items = await _fs.ListAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _all = items.ToList();
            _rows.Clear();
            CurrentPath = path;
            PathInput = path;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (SftpPermissionDeniedException)
        {
            PathInput = CurrentPath;
            Notice?.Invoke($"Sem permissão para abrir {path}", true);
        }
        catch (SftpPathNotFoundException)
        {
            PathInput = CurrentPath;
            Notice?.Invoke($"Pasta não encontrada: {path}", true);
        }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            // Só o SFTP caiu (o terminal segue): reabre uma vez.
            _fs?.Dispose();
            _fs = null;
            CurrentPath = path;
            await EnsureConnectedAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Listar {path}", ex);
            Notice?.Invoke(ex.Message, true);
        }
        finally
        {
            if (_listCts == cts)
            {
                IsBusy = false;
            }
        }
    }

    public Task RefreshAsync() => NavigateAsync(CurrentPath);

    public Task UpAsync() => NavigateAsync(RemotePath.Parent(CurrentPath));

    public Task HomeAsync() => _fs is null ? EnsureConnectedAsync() : NavigateAsync(_fs.HomeDirectory);

    /// <summary>Pasta: entra. Link: entra se apontar para pasta. Arquivo: retorna false (quem chamou abre para edição).</summary>
    public async Task<bool> TryEnterAsync(RemoteEntryViewModel item)
    {
        if (item.IsDirectory)
        {
            await NavigateAsync(item.Entry.FullPath);
            return true;
        }

        if (item.Entry.IsSymlink && _fs is not null && await _fs.IsDirectoryAsync(item.Entry.FullPath, CancellationToken.None))
        {
            await NavigateAsync(item.Entry.FullPath);
            return true;
        }

        return false;
    }

    public void ChangeTerminalDirectory(string path) => _sendToTerminal($"cd {RemotePath.ShellQuote(path)}\r");

    /// <summary>Mostra as últimas linhas do arquivo e acompanha as novas (Ctrl+C para parar).</summary>
    public void FollowInTerminal(string path) => _sendToTerminal($"tail -n 200 -f {RemotePath.ShellQuote(path)}\r");

    public IReadOnlyList<string> ExistingNames(IEnumerable<string> names, string targetDirectory)
    {
        if (targetDirectory != CurrentPath)
        {
            return [];
        }

        var existing = new HashSet<string>(_all.Select(e => e.Name), StringComparer.Ordinal);
        return names.Where(existing.Contains).ToList();
    }

    public async Task CreateFolderAsync(string name) =>
        await RunAsync($"Pasta \"{name}\" criada.", fs => fs.CreateDirectoryAsync(RemotePath.Combine(CurrentPath, name), CancellationToken.None));

    public async Task CreateFileAsync(string name) =>
        await RunAsync($"Arquivo \"{name}\" criado.", fs => fs.CreateEmptyFileAsync(RemotePath.Combine(CurrentPath, name), CancellationToken.None));

    public async Task RenameAsync(RemoteEntryViewModel item, string newName) =>
        await RunAsync($"Renomeado para \"{newName}\".", fs =>
            fs.RenameAsync(item.Entry.FullPath, RemotePath.Combine(RemotePath.Parent(item.Entry.FullPath), newName), CancellationToken.None));

    public async Task DeleteAsync(IReadOnlyList<RemoteEntryViewModel> items) =>
        await RunAsync(items.Count == 1 ? $"\"{items[0].Name}\" excluído." : $"{items.Count} itens excluídos.", async fs =>
        {
            foreach (var item in items)
            {
                await fs.DeleteAsync(item.Entry, CancellationToken.None);
            }
        });

    /// <summary>Envia arquivos e pastas do Windows para <paramref name="targetDirectory"/>.</summary>
    public void Upload(IReadOnlyList<string> localPaths, string targetDirectory)
    {
        foreach (var local in localPaths)
        {
            if (Directory.Exists(local))
            {
                var files = Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList();
                var total = files.Sum(f => new FileInfo(f).Length);
                var remoteRoot = RemotePath.Combine(targetDirectory, Path.GetFileName(local.TrimEnd('\\')));
                _transfers.Enqueue(Path.GetFileName(local.TrimEnd('\\')) + "/", upload: true, total, async item =>
                {
                    var fs = await GetFileSystemAsync();
                    await EnsureRemoteDirectoryAsync(fs, remoteRoot, item.Token);
                    foreach (var dir in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                    {
                        await EnsureRemoteDirectoryAsync(fs, RemotePath.Combine(remoteRoot, ToRemoteRelative(local, dir)), item.Token);
                    }

                    long baseline = 0;
                    foreach (var file in files)
                    {
                        var size = new FileInfo(file).Length;
                        var start = baseline;
                        var target = RemotePath.Combine(remoteRoot, ToRemoteRelative(local, file));
                        // Retomada: arquivos que já chegaram inteiros não são enviados de novo.
                        if (item.Attempt > 1 && await SameRemoteSizeAsync(fs, target, size, item.Token))
                        {
                            baseline += size;
                            item.SetDone(baseline);
                            continue;
                        }

                        await fs.UploadFileAsync(file, target, new CallbackProgress(b => item.SetDone(start + b)), item.Token);
                        baseline += size;
                    }
                }, Session.Name);
            }
            else if (File.Exists(local))
            {
                var name = Path.GetFileName(local);
                _transfers.Enqueue(name, upload: true, new FileInfo(local).Length, async item =>
                {
                    var fs = await GetFileSystemAsync();
                    await fs.UploadFileAsync(local, RemotePath.Combine(targetDirectory, name), new CallbackProgress(item.SetDone), item.Token);
                }, Session.Name);
            }
        }

        RefreshWhenTransfersFinish(targetDirectory);
    }

    /// <summary>Baixa arquivos e pastas para <paramref name="localDirectory"/>. Não sobrescreve: cria "nome (1)".</summary>
    public void Download(IReadOnlyList<RemoteEntryViewModel> items, string localDirectory)
    {
        foreach (var item in items)
        {
            var entry = item.Entry;
            if (entry.IsDirectory)
            {
                var localRoot = UniquePath(Path.Combine(localDirectory, SafeLocalName(entry.Name)));
                _transfers.Enqueue(entry.Name + "/", upload: false, 0, async transfer =>
                {
                    var fs = await GetFileSystemAsync();
                    var files = new List<(RemoteEntry File, string Local)>();
                    await CollectAsync(fs, entry.FullPath, localRoot, files, transfer.Token);
                    transfer.TotalBytes = files.Sum(f => f.File.Size);
                    long baseline = 0;
                    foreach (var (file, local) in files)
                    {
                        var start = baseline;
                        if (transfer.Attempt > 1 && File.Exists(local) && new FileInfo(local).Length == file.Size)
                        {
                            baseline += file.Size; // retomada: já baixado
                            transfer.SetDone(baseline);
                            continue;
                        }

                        transfer.TrackPartial(local);
                        await fs.DownloadFileAsync(file.FullPath, local, new CallbackProgress(b => transfer.SetDone(start + b)), transfer.Token,
                            keepPartial: () => transfer.PauseRequested, resume: transfer.Attempt > 1);
                        baseline += file.Size;
                    }
                }, Session.Name);
            }
            else
            {
                var local = UniquePath(Path.Combine(localDirectory, SafeLocalName(entry.Name)));
                _transfers.Enqueue(entry.Name, upload: false, entry.Size, async transfer =>
                {
                    var fs = await GetFileSystemAsync();
                    transfer.TrackPartial(local);
                    await fs.DownloadFileAsync(entry.FullPath, local, new CallbackProgress(transfer.SetDone), transfer.Token,
                        keepPartial: () => transfer.PauseRequested, resume: transfer.Attempt > 1);
                }, Session.Name);
            }
        }
    }

    private static async Task<bool> SameRemoteSizeAsync(RemoteFileSystem fs, string path, long size, CancellationToken ct)
    {
        try
        {
            return await fs.ExistsAsync(path, ct) && (await fs.GetAsync(path, ct)).Size == size;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_pendingRefresh is { } pending)
        {
            _transfers.Completed -= pending;
            _pendingRefresh = null;
        }

        _listCts?.Cancel();
        _fs?.Dispose();
        _fs = null;
    }

    public static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 ? "arquivo" : safe;
    }

    private async Task ConnectCoreAsync()
    {
        State = FilesState.Connecting;
        Message = "Abrindo SFTP...";
        try
        {
            _fs?.Dispose();
            var fs = await _connect(CancellationToken.None);
            if (_disposed || IsSessionDisconnected)
            {
                // A aba fechou (ou o terminal caiu) enquanto o SFTP abria: não deixa a conexão sobrando.
                fs.Dispose();
                return;
            }

            _fs = fs;
            State = FilesState.Ready;
            Message = "";
            var start = !string.IsNullOrEmpty(CurrentPath) ? CurrentPath : _terminalDirectory ?? _fs.HomeDirectory;
            await NavigateAsync(start);
            if (_all.Count == 0 && CurrentPath != start)
            {
                await NavigateAsync(_fs.HomeDirectory);
            }
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            Log.Error($"SFTP {Session.Endpoint}", ex);
            _fs = null;
            State = FilesState.Error;
            Message = ex is SshException { Message: var m } && m.Contains("subsystem", StringComparison.OrdinalIgnoreCase)
                ? "O servidor não oferece SFTP."
                : SessionConnector.Describe(ex);
        }
    }

    private async Task RunAsync(string success, Func<RemoteFileSystem, Task> action)
    {
        try
        {
            var fs = await GetFileSystemAsync();
            await action(fs);
            Notice?.Invoke(success, false);
        }
        catch (SftpPermissionDeniedException)
        {
            Notice?.Invoke("Sem permissão para esta operação no servidor.", true);
        }
        catch (Exception ex)
        {
            Log.Error("Operação SFTP", ex);
            Notice?.Invoke(ex.Message, true);
        }

        await RefreshAsync();
    }

    private void ApplyFilter()
    {
        var text = Filter?.Trim() ?? "";
        var visible = _all
            .Where(e => ShowHidden || !e.IsHidden)
            .Where(e => text.Length == 0 || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Entries.ReplaceAll(visible.Select(entry =>
        {
            if (!_rows.TryGetValue(entry, out var row))
            {
                row = new RemoteEntryViewModel(entry);
                _rows[entry] = row;
            }

            return row;
        }));

        var dirs = visible.Count(e => e.IsDirectory);
        var files = visible.Count - dirs;
        var bytes = visible.Where(e => !e.IsDirectory).Sum(e => e.Size);
        Summary = $"{dirs} pastas · {files} arquivos · {Format.Bytes(bytes)}" + (visible.Count < _all.Count ? $"  (de {_all.Count})" : "");
    }

    private void RefreshWhenTransfersFinish(string directory)
    {
        void Handler(TransferItem item)
        {
            if (_disposed)
            {
                // Aba fechada com envio na fila: solta o painel (e a sessão presa por ele) já.
                _transfers.Completed -= Handler;
                _pendingRefresh = null;
                return;
            }

            if (_transfers.Items.Any(i => i.IsActive && i.IsUpload))
            {
                return;
            }

            _transfers.Completed -= Handler;
            _pendingRefresh = null;
            if (directory == CurrentPath)
            {
                _ = RefreshAsync();
            }
        }

        if (_pendingRefresh is { } old)
        {
            _transfers.Completed -= old; // um por vez: vários envios seguidos não empilham inscrições
        }

        _pendingRefresh = Handler;
        _transfers.Completed += Handler;
    }

    private Action<TransferItem>? _pendingRefresh;

    private static async Task EnsureRemoteDirectoryAsync(RemoteFileSystem fs, string path, CancellationToken ct)
    {
        if (!await fs.ExistsAsync(path, ct))
        {
            await fs.CreateDirectoryAsync(path, ct);
        }
    }

    private static async Task CollectAsync(RemoteFileSystem fs, string remoteDir, string localDir, List<(RemoteEntry, string)> files, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);
        foreach (var child in await fs.ListAsync(remoteDir, ct))
        {
            var local = Path.Combine(localDir, SafeLocalName(child.Name));
            if (child.IsDirectory && !child.IsSymlink)
            {
                await CollectAsync(fs, child.FullPath, local, files, ct);
            }
            else if (!child.IsDirectory)
            {
                files.Add((child, local));
            }
        }
    }

    private static string ToRemoteRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string UniquePath(string path)
    {
        if (!RootDock.Core.Sftp.LocalFiles.IsTaken(path))
        {
            return path;
        }

        return RootDock.Core.Sftp.LocalFiles.UniquePath(path);
    }

    private static bool IsConnectionLost(Exception ex) =>
        ex is SshConnectionException or ObjectDisposedException or SocketException ||
        (ex is InvalidOperationException && ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase));
}

public sealed class CallbackProgress(Action<long> handler) : IProgress<long>
{
    public void Report(long value) => handler(value);
}
