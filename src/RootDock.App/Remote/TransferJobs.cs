using System.IO;
using RootDock.App.Services;
using RootDock.Core.Sftp;

namespace RootDock.App.Remote;

/// <summary>De onde vêm os arquivos do lado "servidor" do gerenciador (SFTP de um terminal ou FTP/FTPS).</summary>
public sealed class FileManagerSource
{
    /// <summary>Nome do servidor (título da janela e histórico).</summary>
    public required string Name { get; init; }

    public required bool IsProduction { get; init; }

    public required string StartPath { get; init; }

    /// <summary>"SFTP", "FTP" ou "FTPS".</summary>
    public required string Protocol { get; init; }

    /// <summary>Conexão de arquivos (reconecta se preciso).</summary>
    public required Func<Task<IFileTransport>> Connect { get; init; }

    /// <summary>Conexão SSH para comandos (du, tar, chown). Null no FTP.</summary>
    public Func<Renci.SshNet.SshClient?>? Ssh { get; init; }

    /// <summary>Abrir no editor do RootDock. Null quando não disponível (FTP).</summary>
    public Func<RemoteEntry, Task>? Edit { get; init; }

    /// <summary>
    /// Servidor Windows (SMB): caminho de rede do arquivo (\\servidor\C$\...). Abre direto no
    /// programa do Windows, e salvar já grava no servidor. Null nos outros protocolos.
    /// </summary>
    public Func<RemoteEntry, string>? WindowsPath { get; init; }

    /// <summary>Caminho como a pessoa lê (SMB: \\servidor\C$\pasta). Null = o próprio caminho.</summary>
    public Func<string, string>? ShowPath { get; init; }

    /// <summary>Caminho digitado → caminho do gerenciador (SMB aceita C:\pasta); null = inválido.</summary>
    public Func<string, string?>? ReadPath { get; init; }

    /// <summary>Chamado ao fechar a janela (FTP: encerra a conexão).</summary>
    public Action? Closed { get; init; }
}

/// <summary>
/// Envio e download pela fila de transferências, para qualquer protocolo: pastas inteiras, pausa
/// e retomada (download continua do parcial; na retomada pula o que já está completo).
/// </summary>
public static class TransferJobs
{
    /// <summary>Envia arquivos e pastas do computador para <paramref name="targetDirectory"/>.</summary>
    public static void Upload(TransferQueue queue, FileManagerSource source, IReadOnlyList<string> localPaths, string targetDirectory)
    {
        foreach (var local in localPaths)
        {
            var name = Path.GetFileName(local.TrimEnd('\\'));
            if (Directory.Exists(local))
            {
                var files = Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList();
                var total = files.Sum(f => new FileInfo(f).Length);
                var remoteRoot = RemotePath.Combine(targetDirectory, name);
                queue.Enqueue(name + "/", upload: true, total, async item =>
                {
                    var fs = await source.Connect();
                    await EnsureDirectoryAsync(fs, remoteRoot, item.Token);
                    foreach (var dir in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                    {
                        await EnsureDirectoryAsync(fs, RemotePath.Combine(remoteRoot, Relative(local, dir)), item.Token);
                    }

                    long baseline = 0;
                    foreach (var file in files)
                    {
                        var size = new FileInfo(file).Length;
                        var start = baseline;
                        var target = RemotePath.Combine(remoteRoot, Relative(local, file));
                        if (item.Attempt > 1 && await SameSizeAsync(fs, target, size, item.Token))
                        {
                            baseline += size;
                            item.SetDone(baseline);
                            continue;
                        }

                        await fs.UploadFileAsync(file, target, new Progress(b => item.SetDone(start + b)), item.Token);
                        baseline += size;
                    }
                }, source.Name);
            }
            else if (File.Exists(local))
            {
                queue.Enqueue(name, upload: true, new FileInfo(local).Length, async item =>
                {
                    var fs = await source.Connect();
                    await fs.UploadFileAsync(local, RemotePath.Combine(targetDirectory, name), new Progress(item.SetDone), item.Token);
                }, source.Name);
            }
        }
    }

    /// <summary>Baixa para <paramref name="localDirectory"/>. Não sobrescreve: o que já existe ganha "nome (1)".</summary>
    public static void Download(TransferQueue queue, FileManagerSource source, IReadOnlyList<RemoteEntry> entries, string localDirectory)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                var localRoot = UniquePath(Path.Combine(localDirectory, RemoteFilesViewModel.SafeLocalName(entry.Name)));
                queue.Enqueue(entry.Name + "/", upload: false, 0, async transfer =>
                {
                    var fs = await source.Connect();
                    var files = new List<(RemoteEntry File, string Local)>();
                    await CollectAsync(fs, entry.FullPath, localRoot, files, 0, transfer.Token);
                    transfer.TotalBytes = files.Sum(f => f.File.Size);
                    Directory.CreateDirectory(localRoot);
                    long baseline = 0;
                    foreach (var (file, local) in files)
                    {
                        var start = baseline;
                        if (transfer.Attempt > 1 && File.Exists(local) && new FileInfo(local).Length == file.Size)
                        {
                            baseline += file.Size;
                            transfer.SetDone(baseline);
                            continue;
                        }

                        transfer.TrackPartial(local);
                        await fs.DownloadFileAsync(file.FullPath, local, new Progress(b => transfer.SetDone(start + b)), transfer.Token,
                            () => transfer.PauseRequested, resume: transfer.Attempt > 1);
                        baseline += file.Size;
                    }
                }, source.Name);
            }
            else
            {
                var local = UniquePath(Path.Combine(localDirectory, RemoteFilesViewModel.SafeLocalName(entry.Name)));
                queue.Enqueue(entry.Name, upload: false, entry.Size, async transfer =>
                {
                    var fs = await source.Connect();
                    transfer.TrackPartial(local);
                    await fs.DownloadFileAsync(entry.FullPath, local, new Progress(transfer.SetDone), transfer.Token,
                        () => transfer.PauseRequested, resume: transfer.Attempt > 1);
                }, source.Name);
            }
        }
    }

    public static async Task EnsureDirectoryAsync(IFileTransport fs, string path, CancellationToken ct)
    {
        if (path is "/" or "" || await fs.ExistsAsync(path, ct))
        {
            return;
        }

        await EnsureDirectoryAsync(fs, RemotePath.Parent(path), ct);
        await fs.CreateDirectoryAsync(path, ct);
    }

    private static async Task CollectAsync(IFileTransport fs, string remote, string local, List<(RemoteEntry, string)> files, int depth, CancellationToken ct)
    {
        if (depth > 32)
        {
            return;
        }

        Directory.CreateDirectory(local);
        foreach (var child in await fs.ListAsync(remote, ct))
        {
            var target = Path.Combine(local, RemoteFilesViewModel.SafeLocalName(child.Name));
            if (child.IsDirectory && !child.IsSymlink)
            {
                await CollectAsync(fs, child.FullPath, target, files, depth + 1, ct);
            }
            else if (!child.IsDirectory)
            {
                files.Add((child, target));
            }
        }
    }

    private static async Task<bool> SameSizeAsync(IFileTransport fs, string path, long size, CancellationToken ct)
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

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string UniquePath(string path) => LocalFiles.UniquePath(path);

    private sealed class Progress(Action<long> handler) : IProgress<long>
    {
        public void Report(long value) => handler(value);
    }
}
