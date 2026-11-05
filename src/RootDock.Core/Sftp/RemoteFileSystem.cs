using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using RootDock.Core.Security;
using RootDock.Core.Ssh;

namespace RootDock.Core.Sftp;

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTime Modified,
    string Permissions,
    int UserId,
    int GroupId)
{
    public bool IsHidden => Name.StartsWith('.');
}

/// <summary>
/// Arquivos do servidor via SFTP, numa conexão própria com as mesmas credenciais do terminal.
/// Fechar (<see cref="Dispose"/>) cancela o que estiver em andamento e só descarta o cliente
/// depois da última operação terminar, sempre fora da thread de quem chamou.
/// </summary>
public sealed class RemoteFileSystem : IFileTransport
{
    private readonly SftpClient _client;
    private readonly List<IDisposable> _owned;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private int _active;
    private bool _closing;

    private RemoteFileSystem(SftpClient client, List<IDisposable> owned)
    {
        _client = client;
        _owned = owned;
        HomeDirectory = client.WorkingDirectory;
    }

    public string HomeDirectory { get; }

    public string Protocol => "SFTP";

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                if (_closing)
                {
                    return false;
                }
            }

            try
            {
                return _client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <param name="connectHost">Endereço real (túnel do bastion); null = o próprio servidor.</param>
    public static async Task<RemoteFileSystem> ConnectAsync(
        string host, int port, SshCredentials credentials, KnownHostsStore knownHosts, ISshInteraction interaction, CancellationToken ct,
        string? connectHost = null, int? connectPort = null)
    {
        var owned = new List<IDisposable>();
        var info = SshClientFactory.CreateConnectionInfo(connectHost ?? host, connectPort ?? port, credentials, interaction, owned);
        var client = new SftpClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(30),
        };
        var rejected = false;
        SshClientFactory.AttachHostKeyVerification(client, host, port, knownHosts, interaction, () => rejected = true);
        try
        {
            await SshClientFactory.ConnectAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            foreach (var o in owned)
            {
                o.Dispose();
            }

            if (ex is SshConnectionException && rejected)
            {
                throw new HostKeyRejectedException(host);
            }

            throw;
        }

        return new RemoteFileSystem(client, owned);
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) =>
        RunAsync<IReadOnlyList<RemoteEntry>>(ct, async token =>
        {
            var list = new List<RemoteEntry>();
            await foreach (var file 
            in _client.ListDirectoryAsync(path, token).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                list.Add(ToEntry(file));
            }

            return list;
        });

    public Task<RemoteEntry> GetAsync(string path, CancellationToken ct) =>
        RunAsync(ct, async token => ToEntry(await _client.GetAsync(path, token).ConfigureAwait(false)));

    /// <summary>Se o caminho (ou o destino do link) é uma pasta.</summary>
    public Task<bool> IsDirectoryAsync(string path, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            try
            {
                var attributes = await _client.GetAttributesAsync(path, token).ConfigureAwait(false);
                return attributes.IsDirectory;
            }
            catch (SftpPathNotFoundException)
            {
                return false;
            }
        });

    /// <summary>
    /// Baixa para "nome.rootdock-part" e renomeia no fim. Com <paramref name="keepPartial"/>
    /// verdadeiro no cancelamento (pausa), o parcial fica; com <paramref name="resume"/> (retomada
    /// da mesma transferência) a próxima chamada continua dele. Parcial de outra transferência é
    /// descartado. Sem <paramref name="overwrite"/>, nunca substitui um arquivo existente.
    /// </summary>
    public Task<string> DownloadFileAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct,
        Func<bool>? keepPartial = null, bool resume = false, bool overwrite = false) =>
        RunAsync(ct, async token =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var temp = localPath + LocalFiles.PartialSuffix;
            try
            {
                var resumeFrom = resume && File.Exists(temp) ? new FileInfo(temp).Length : 0;
                if (resumeFrom > 0)
                {
                    var remoteSize = _client.Get(remotePath).Length;
                    if (resumeFrom >= remoteSize)
                    {
                        resumeFrom = 0; // parcial não confere com o arquivo do servidor: recomeça
                    }
                }

                if (resumeFrom > 0)
                {
                    await using var local = new FileStream(temp, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await using var remote = await _client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
                    remote.Seek(resumeFrom, SeekOrigin.Begin);
                    var buffer = new byte[1 << 16];
                    var done = resumeFrom;
                    progress?.Report(done);
                    int read;
                    while ((read = await remote.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await local.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        done += read;
                        progress?.Report(done);
                    }
                }
                else
                {
                    await using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    var report = progress is null
                        ? null
                        : new DirectProgress<DownloadFileProgressReport>(r => progress.Report((long)r.TotalBytesDownloaded));
                    await _client.DownloadFileAsync(remotePath, stream, report, token).ConfigureAwait(false);
                }

                return LocalFiles.Commit(temp, localPath, overwrite);
            }
            catch (OperationCanceledException) when (keepPartial?.Invoke() == true)
            {
                throw; // pausa: o parcial fica para continuar
            }
            catch
            {
                TryDeleteLocal(temp);
                throw;
            }
        });

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            var report = progress is null
                ? null
                : new DirectProgress<UploadFileProgressReport>(r => progress.Report((long)r.TotalBytesUploaded));
            await _client.UploadFileAsync(stream, remotePath, true, report, token).ConfigureAwait(false);
            return true;
        });

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            await _client.CreateDirectoryAsync(path, token).ConfigureAwait(false);
            return true;
        });

    public Task CreateEmptyFileAsync(string path, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            if (await _client.ExistsAsync(path, token).ConfigureAwait(false))
            {
                throw new IOException($"Já existe um item chamado '{RemotePath.Name(path)}'.");
            }

            await using var stream = await _client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, token).ConfigureAwait(false);
            return true;
        });

    public Task RenameAsync(string from, string to, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            await _client.RenameFileAsync(from, to, token).ConfigureAwait(false);
            return true;
        });

    /// <summary>Lê o arquivo inteiro (para o editor). Recusa arquivos maiores que o limite.</summary>
    public Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            var attributes = await _client.GetAttributesAsync(path, token).ConfigureAwait(false);
            if (attributes.Size > maxBytes)
            {
                throw new IOException($"Arquivo grande demais para o editor ({attributes.Size / 1024 / 1024} MB). Baixe ou abra num programa externo.");
            }

            await using var stream = await _client.OpenAsync(path, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
            using var buffer = new MemoryStream((int)Math.Max(0, attributes.Size));
            await stream.CopyToAsync(buffer, 1 << 16, token).ConfigureAwait(false);
            if (buffer.Length > maxBytes)
            {
                throw new IOException("Arquivo grande demais para o editor.");
            }

            return buffer.ToArray();
        });

    /// <summary>
    /// Grava o conteúdo no próprio arquivo (truncando). Não cria cópia temporária nem renomeia:
    /// dono, grupo e permissões continuam os mesmos (importante em arquivos de configuração).
    /// </summary>
    public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct) =>
        RunAsync(ct, async token =>
        {
            await using var stream = await _client.OpenAsync(path, FileMode.Create, FileAccess.Write, token).ConfigureAwait(false);
            await stream.WriteAsync(content, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            return true;
        });

    public Task<bool> ExistsAsync(string path, CancellationToken ct) =>
        RunAsync(ct, token => _client.ExistsAsync(path, token));

    public Task ChangePermissionsAsync(string path, short mode, CancellationToken ct) =>
        RunAsync(ct, token => Task.Run(() =>
        {
            _client.ChangePermissions(path, mode);
            return true;
        }, token));

    /// <summary>Exclui um arquivo, link ou pasta (com todo o conteúdo). Links nunca são seguidos.</summary>
    public async Task DeleteAsync(RemoteEntry entry, CancellationToken ct)
    {
        if (entry.IsDirectory && !entry.IsSymlink)
        {
            foreach (var child in await ListAsync(entry.FullPath, ct).ConfigureAwait(false))
            {
                await DeleteAsync(child, ct).ConfigureAwait(false);
            }

            await RunAsync(ct, async token =>
            {
                await _client.DeleteDirectoryAsync(entry.FullPath, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        else
        {
            await RunAsync(ct, async token =>
            {
                await _client.DeleteFileAsync(entry.FullPath, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancela as operações em andamento e fecha a conexão quando a última terminar.
    /// Não bloqueia: desconectar envolve rede e nunca deve segurar a interface.
    /// </summary>
    public void Dispose()
    {
        bool closeNow;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            closeNow = _active == 0;
        }

        _lifetime.Cancel();
        if (closeNow)
        {
            CloseInBackground();
        }
    }

    /// <summary>
    /// Toda chamada ao SftpClient passa por aqui: conta as operações ativas (o cliente só é descartado
    /// depois da última) e converte falhas causadas pelo fechamento em cancelamento.
    /// </summary>
    private async Task<T> RunAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        lock (_gate)
        {
            if (_closing)
            {
                throw new OperationCanceledException("Conexão SFTP fechada.");
            }

            _active++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            try
            {
                return await action(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && linked.IsCancellationRequested)
            {
                throw new OperationCanceledException("Operação cancelada.", ex, linked.Token);
            }
        }
        finally
        {
            bool closeNow;
            lock (_gate)
            {
                _active--;
                closeNow = _closing && _active == 0;
            }

            if (closeNow)
            {
                CloseInBackground();
            }
        }
    }

    private void CloseInBackground() => _ = Task.Run(() =>
    {
        try
        {
            _client.Disconnect();
        }
        catch (Exception)
        {
            // Conexão já caída: nada a fazer.
        }

        _client.Dispose();
        foreach (var o in _owned)
        {
            o.Dispose();
        }
    });

    private static RemoteEntry ToEntry(ISftpFile file) => new(
        file.Name,
        file.FullName,
        file.IsDirectory,
        file.IsSymbolicLink,
        file.Length,
        file.LastWriteTime,
        PermissionString(file),
        file.UserId,
        file.GroupId);

    private static string PermissionString(ISftpFile f)
    {
        var type = f.IsDirectory ? 'd' : f.IsSymbolicLink ? 'l' : '-';
        return string.Concat(
            type,
            f.OwnerCanRead ? 'r' : '-', f.OwnerCanWrite ? 'w' : '-', f.OwnerCanExecute ? 'x' : '-',
            f.GroupCanRead ? 'r' : '-', f.GroupCanWrite ? 'w' : '-', f.GroupCanExecute ? 'x' : '-',
            f.OthersCanRead ? 'r' : '-', f.OthersCanWrite ? 'w' : '-', f.OthersCanExecute ? 'x' : '-');
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Progresso entregue na própria thread da transferência (quem exibe decide a frequência).</summary>
internal sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>Caminhos POSIX do servidor (sempre com "/").</summary>
public static class RemotePath
{
    public static string Combine(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public static string Parent(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>Entre aspas simples para colar no shell com segurança.</summary>
    public static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
