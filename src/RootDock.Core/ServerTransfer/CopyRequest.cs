namespace RootDock.Core.ServerTransfer;

/// <summary>Um item a copiar, com o caminho relativo à pasta de origem.</summary>
public sealed record CopyItem(string RelativePath, bool IsDirectory, long Size);

/// <summary>
/// O que copiar de um servidor para outro: os itens ficam em <see cref="SourceRoot"/> e vão para
/// <see cref="TargetRoot"/> com o mesmo caminho relativo. Pastas vão com todo o conteúdo.
/// </summary>
public sealed record CopyRequest(string SourceRoot, string TargetRoot, IReadOnlyList<CopyItem> Items)
{
    /// <summary>Mantém data de modificação e permissões (no modo direto, também dono e grupo).</summary>
    public bool Preserve { get; init; } = true;

    /// <summary>Não mexe no que já existe no destino (o padrão é atualizar).</summary>
    public bool SkipExisting { get; init; }
}

/// <summary>Resultado de uma cópia pelo RootDock (SFTP).</summary>
public sealed record RelaySummary(int Copied, int Skipped, int SkippedLinks);

/// <summary>O modo direto (rsync entre os servidores) não está disponível; a mensagem diz por quê.</summary>
public sealed class DirectUnavailableException(string reason, Exception? inner = null) : Exception(reason, inner);
