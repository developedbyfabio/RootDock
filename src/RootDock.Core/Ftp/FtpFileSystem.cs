using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using RootDock.Core.Sftp;

namespace RootDock.Core.Ftp;

/// <summary>Criptografia do FTP.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<FtpSecurity>))]
public enum FtpSecurity
{
    /// <summary>FTP puro (sem criptografia: usuário e senha passam em texto).</summary>
    None,

    /// <summary>FTPS explícito (AUTH TLS na porta 21) — o mais comum.</summary>
    Explicit,

    /// <summary>FTPS implícito (TLS desde o início, porta 990).</summary>
    Implicit,
}

/// <summary>Opções de uma sessão FTP/FTPS.</summary>
public sealed record FtpSettings
{
    public FtpSecurity Security { get; init; } = FtpSecurity.Explicit;

    /// <summary>Impressão digital (SHA-256) do certificado aceito pelo usuário (autoassinado ou de CA interna).</summary>
    public string? TrustedCertificate { get; init; }

    /// <summary>Modo passivo (o padrão; ativo só em redes antigas).</summary>
    public bool Passive { get; init; } = true;
}

/// <summary>O servidor apresentou um certificado que o Windows não confia (o usuário decide).</summary>
public sealed class FtpCertificateException(string thumbprint, string subject, string issuer, DateTime notAfter, string problem)
    : Exception($"O certificado do servidor FTPS não é confiável: {problem}")
{
    public string Thumbprint { get; } = thumbprint;

    public string Subject { get; } = subject;

    public string Issuer { get; } = issuer;

    public DateTime NotAfter { get; } = notAfter;

    public string Problem { get; } = problem;
}

/// <summary>FTP e FTPS (FluentFTP) com a mesma interface do SFTP, para o gerenciador de arquivos.</summary>
public sealed class FtpFileSystem : IFileTransport
{
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1); // uma operação por vez no canal de controle
    private readonly CancellationTokenSource _life = new();
    private int _active;
    private int _disposed;
    private int _clientDisposed;

    private FtpFileSystem(AsyncFtpClient client, string home, FtpSecurity security)
    {
        _client = client;
        HomeDirectory = home;
        Protocol = security == FtpSecurity.None ? "FTP" : "FTPS";
    }

    public string HomeDirectory { get; }

    public string Protocol { get; }

    public bool IsConnected => _client.IsConnected;

    public static async Task<FtpFileSystem> ConnectAsync(
        string host, int port, string user, string? password, FtpSettings settings, CancellationToken ct)
    {
        var client = new AsyncFtpClient(host, string.IsNullOrEmpty(user) ? "anonymous" : user, password ?? "", port);
        client.Config.EncryptionMode = settings.Security switch
        {
            FtpSecurity.Implicit => FtpEncryptionMode.Implicit,
            FtpSecurity.Explicit => FtpEncryptionMode.Explicit,
            _ => FtpEncryptionMode.None,
        };
        client.Config.DataConnectionEncryption = settings.Security != FtpSecurity.None;
        client.Config.DataConnectionType = settings.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 15000;
        client.Config.DataConnectionReadTimeout = 30000;
        client.Config.RetryAttempts = 1;
        client.Config.TimeConversion = FtpDate.LocalTime; // datas no fuso deste computador, como no SFTP

        FtpCertificateException? rejected = null;
        client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }

            var cert = new X509Certificate2(e.Certificate);
            var thumb = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            if (settings.TrustedCertificate is { } trusted && string.Equals(trusted, thumb, StringComparison.OrdinalIgnoreCase))
            {
                e.Accept = true; // aceito antes pelo usuário (mesmo certificado)
                return;
            }

            var problem = e.PolicyErrors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)
                ? "o nome no certificado não é o deste servidor"
                : cert.NotAfter < DateTime.Now ? "o certificado está vencido"
                : "emitido por uma autoridade que o Windows não conhece (autoassinado ou de CA interna)";
            rejected = new FtpCertificateException(thumb, cert.Subject, cert.Issuer, cert.NotAfter, problem);
            e.Accept = false;
        };

        try
        {
            await client.Connect(ct).ConfigureAwait(false);
            var home = await client.GetWorkingDirectory(ct).ConfigureAwait(false);
            return new FtpFileSystem(client, string.IsNullOrEmpty(home) ? "/" : home, settings.Security);
        }
        catch (Exception ex) when (rejected is not null && ex is AuthenticationException or FtpException or IOException)
        {
            client.Dispose();
            throw rejected;
        }
        catch (FtpAuthenticationException ex)
        {
            client.Dispose();
            throw new UnauthorizedAccessException($"Usuário ou senha recusados pelo servidor FTP ({ex.Message.Trim()}).", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<T> Run<T>(Func<Task<T>> action, CancellationToken ct)
    {
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token))
        {
            try
            {
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(FtpFileSystem), "A conexão FTP foi fechada.");
            }
        }

        Interlocked.Increment(ref _active);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposed) != 0)
            {
                DisposeClient(); // fechado durante a operação: descarta agora que ela terminou
            }
        }
    }

    private Task Run(Func<Task> action, CancellationToken ct) => Run(async () =>
    {
        await action().ConfigureAwait(false);
        return true;
    }, ct);

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run<IReadOnlyList<RemoteEntry>>(async () =>
    {
        var items = await _client.GetListing(path, FtpListOption.AllFiles, ct).ConfigureAwait(false);
        return items.Where(i => i.Name is not ("." or "..")).Select(ToEntry).ToList();
    }, ct);

    public Task<RemoteEntry> GetAsync(string path, CancellationToken ct) => Run(async () =>
    {
        var item = await _client.GetObjectInfo(path, dateModified: true, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Não encontrado no servidor: {path}");
        return ToEntry(item);
    }, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Run(async () =>
        await _client.FileExists(path, ct).ConfigureAwait(false) || await _client.DirectoryExists(path, ct).ConfigureAwait(false), ct);

    public Task<string> DownloadFileAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct,
        Func<bool>? keepPartial = null, bool resume = false, bool overwrite = false) => Run(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var temp = localPath + LocalFiles.PartialSuffix;
        try
        {
            var resumeFrom = resume && File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (resumeFrom > 0)
            {
                // Como no SFTP e no SMB: se o arquivo do servidor mudou (ficou menor ou igual ao
                // parcial), o parcial não serve mais e o download recomeça do zero.
                var remoteSize = await _client.GetFileSize(remotePath, -1, ct).ConfigureAwait(false);
                if (remoteSize < 0 || resumeFrom >= remoteSize)
                {
                    resumeFrom = 0;
                }
            }

            await using (var stream = new FileStream(temp, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var report = progress is null ? null : new Progress<FtpProgress>(p => progress.Report(p.TransferredBytes + resumeFrom));
                var ok = await _client.DownloadStream(stream, remotePath, resumeFrom, report, ct).ConfigureAwait(false);
                if (!ok)
                {
                    throw new IOException($"O servidor não entregou {remotePath}.");
                }
            }

            return LocalFiles.Commit(temp, localPath, overwrite);
        }
        catch (OperationCanceledException) when (keepPartial?.Invoke() == true)
        {
            throw; // pausa: o parcial fica para continuar
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct) => Run(async () =>
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        var report = progress is null ? null : new Progress<FtpProgress>(p => progress.Report(p.TransferredBytes));
        var status = await _client.UploadStream(stream, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, report, ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
        {
            throw new IOException($"O servidor recusou o envio de {remotePath}.");
        }
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(() => _client.CreateDirectory(path, force: true, ct), ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(() => _client.Rename(from, to, ct), ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => Run(() =>
        entry.IsDirectory ? _client.DeleteDirectory(entry.FullPath, ct) : _client.DeleteFile(entry.FullPath, ct), ct);

    private static RemoteEntry ToEntry(FtpListItem item) => new(
        item.Name,
        item.FullName,
        item.Type == FtpObjectType.Directory,
        item.Type == FtpObjectType.Link,
        Math.Max(0, item.Size),
        item.Modified == DateTime.MinValue ? default : item.Modified,
        item.RawPermissions ?? "",
        0,
        0);

    /// <summary>
    /// Fechar com operação em andamento: quem espera na fila é liberado na hora (cancelado), a
    /// operação em curso termina e só então o cliente é descartado. Antes, descartar o cliente e o
    /// semáforo no meio quebrava o download, escondia o erro real e deixava a fila presa para sempre.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _life.Cancel();
        if (Volatile.Read(ref _active) == 0)
        {
            DisposeClient();
        }
    }

    private void DisposeClient()
    {
        if (Interlocked.Exchange(ref _clientDisposed, 1) == 0)
        {
            _client.Dispose();
        }
    }
}
