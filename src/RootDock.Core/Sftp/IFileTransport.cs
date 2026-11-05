namespace RootDock.Core.Sftp;

/// <summary>
/// Arquivos de um servidor, independente do protocolo (SFTP pelo SSH, ou FTP/FTPS). O
/// gerenciador de arquivos em duas colunas trabalha só com isto.
/// </summary>
public interface IFileTransport : IDisposable
{
    /// <summary>Pasta inicial do usuário no servidor.</summary>
    string HomeDirectory { get; }

    bool IsConnected { get; }

    /// <summary>"SFTP", "FTP" ou "FTPS" (para a tela).</summary>
    string Protocol { get; }

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct);

    Task<RemoteEntry> GetAsync(string path, CancellationToken ct);

    Task<bool> ExistsAsync(string path, CancellationToken ct);

    /// <summary>
    /// Baixa para "nome.rootdock-part" e renomeia no fim. <paramref name="resume"/>: continua de um
    /// parcial desta mesma transferência (pausada). <paramref name="overwrite"/>: falso = se o
    /// destino já existir, salva como "nome (1)". Retorna o caminho final.
    /// </summary>
    Task<string> DownloadFileAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct,
        Func<bool>? keepPartial = null, bool resume = false, bool overwrite = false);

    Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);

    Task RenameAsync(string from, string to, CancellationToken ct);

    Task DeleteAsync(RemoteEntry entry, CancellationToken ct);
}
