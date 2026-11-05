using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using RootDock.App.Services;
using RootDock.Core.Sftp;

namespace RootDock.App.Remote;

/// <summary>
/// Abre arquivos do servidor no editor do Windows (VS Code quando instalado) e envia de volta
/// automaticamente a cada salvamento. Avisa se o arquivo mudou no servidor nesse meio-tempo
/// e pede confirmação em sessões de produção.
/// </summary>
public sealed class RemoteEditService : IDisposable
{
    private static readonly HashSet<string> BinaryExt =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".gz", ".tgz", ".tar", ".7z", ".rar", ".mp3", ".mp4", ".avi", ".mkv", ".exe", ".dll", ".so", ".bin", ".iso", ".jar", ".war",
    ];

    private readonly Dispatcher _dispatcher;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, bool> _notice;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RootDock", "edit");
    private readonly List<EditSession> _sessions = [];

    public RemoteEditService(Dispatcher dispatcher, Func<string, string, Task<bool>> confirm, Action<string, bool> notice)
    {
        _dispatcher = dispatcher;
        _confirm = confirm;
        _notice = notice;
        CleanupOldCopies();
    }

    public static string? VsCodePath { get; } = FindVsCode();

    public async Task OpenAsync(RemoteFilesViewModel files, RemoteEntry entry)
    {
        if (entry.Size > 50L * 1024 * 1024 &&
            !await _confirm("Arquivo grande", $"{entry.Name} tem {Format.Bytes(entry.Size)}. Baixar e abrir mesmo assim?"))
        {
            return;
        }

        var existing = _sessions.FirstOrDefault(s => s.Files == files && s.RemotePath == entry.FullPath);
        if (existing is not null && File.Exists(existing.LocalPath))
        {
            Launch(existing.LocalPath);
            return;
        }

        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        var local = Path.Combine(dir, RemoteFilesViewModel.SafeLocalName(entry.Name));
        _notice($"Baixando {entry.Name}...", false);
        var fs = await files.GetFileSystemAsync();
        await fs.DownloadFileAsync(entry.FullPath, local, null, CancellationToken.None);

        var session = new EditSession(this, files, entry.FullPath, local, entry.Modified, Hash(local));
        _sessions.Add(session);
        Launch(local);
        _notice($"{entry.Name} aberto. Ao salvar, o RootDock envia de volta ao servidor.", false);
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>Fecha a edição dos arquivos de um painel (sessão encerrada).</summary>
    public void Forget(RemoteFilesViewModel files)
    {
        foreach (var session in _sessions.Where(s => s.Files == files).ToList())
        {
            session.Dispose();
            _sessions.Remove(session);
        }
    }

    public static void Launch(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (VsCodePath is not null && !BinaryExt.Contains(ext))
            {
                var start = new ProcessStartInfo(VsCodePath) { UseShellExecute = false };
                start.ArgumentList.Add(path);
                Process.Start(start);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Sem programa associado à extensão: Bloco de Notas.
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
    }

    private async Task UploadAsync(EditSession s)
    {
        var name = RemotePath.Name(s.RemotePath);
        var hash = Hash(s.LocalPath);
        if (hash is null || hash == s.LastHash)
        {
            return;
        }

        try
        {
            if (s.Files.IsProduction &&
                !await _confirm("Enviar para PRODUÇÃO?", $"Salvar \"{s.RemotePath}\" no servidor de produção {s.Files.Session.Name}?"))
            {
                _notice($"{name}: envio cancelado (a cópia local continua salva).", true);
                return;
            }

            var fs = await s.Files.GetFileSystemAsync();
            var remote = await fs.GetAsync(s.RemotePath, CancellationToken.None);
            if (Math.Abs((remote.Modified - s.RemoteModified).TotalSeconds) > 1 &&
                !await _confirm("Arquivo alterado no servidor",
                    $"\"{s.RemotePath}\" foi modificado no servidor depois que você abriu. Sobrescrever com a sua versão?"))
            {
                _notice($"{name}: envio cancelado para não perder a alteração do servidor.", true);
                return;
            }

            await fs.UploadFileAsync(s.LocalPath, s.RemotePath, null, CancellationToken.None);
            s.LastHash = hash;
            s.RemoteModified = (await fs.GetAsync(s.RemotePath, CancellationToken.None)).Modified;
            _notice($"✓ {name} enviado ao servidor ({DateTime.Now:HH:mm:ss})", false);
            if (RemotePath.Parent(s.RemotePath) == s.Files.CurrentPath)
            {
                _ = s.Files.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Enviar edição {s.RemotePath}", ex);
            _notice($"{name}: falha ao enviar — {ex.Message}", true);
        }
    }

    private static string? Hash(string path)
    {
        // O editor pode estar gravando: tenta algumas vezes.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (IOException)
            {
                Thread.Sleep(60);
            }
        }

        return null;
    }

    private static string? FindVsCode()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private void CleanupOldCopies()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-2))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class EditSession : IDisposable
    {
        private readonly RemoteEditService _owner;
        private readonly FileSystemWatcher _watcher;
        private readonly DispatcherTimer _debounce;
        private bool _uploading;

        public EditSession(RemoteEditService owner, RemoteFilesViewModel files, string remotePath, string localPath, DateTime remoteModified, string? hash)
        {
            _owner = owner;
            Files = files;
            RemotePath = remotePath;
            LocalPath = localPath;
            RemoteModified = remoteModified;
            LastHash = hash;

            // Editores costumam salvar trocando o arquivo (rename): observa a pasta inteira.
            _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, OnDebounce, owner._dispatcher);
            _debounce.Stop();
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
        }

        public RemoteFilesViewModel Files { get; }

        public string RemotePath { get; }

        public string LocalPath { get; }

        public DateTime RemoteModified { get; set; }

        public string? LastHash { get; set; }

        public void Dispose()
        {
            _watcher.Dispose();
            _debounce.Stop();
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!string.Equals(e.FullPath, LocalPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _owner._dispatcher.BeginInvoke(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
        }

        private async void OnDebounce(object? sender, EventArgs e)
        {
            _debounce.Stop();
            if (_uploading)
            {
                _debounce.Start();
                return;
            }

            _uploading = true;
            try
            {
                await _owner.UploadAsync(this);
            }
            finally
            {
                _uploading = false;
            }
        }
    }
}
