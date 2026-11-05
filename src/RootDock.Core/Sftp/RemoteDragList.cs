using RootDock.Core.Shell;

namespace RootDock.Core.Sftp;

/// <summary>
/// Monta a lista de arquivos virtuais para arrastar itens do servidor para o Explorer: pastas são
/// percorridas (links para pastas lá dentro ficam de fora, para não entrar em ciclo) e cada arquivo
/// é lido do servidor só quando o Windows pede o conteúdo.
/// </summary>
public static class RemoteDragList
{
    public const int MaxItems = 50_000;
    public const int MaxDepth = 32;
    private const int MaxPathChars = 259;

    public static async Task<VirtualFileList> BuildAsync(RemoteFileSystem fs, IReadOnlyList<RemoteEntry> entries, CancellationToken ct)
    {
        var scan = new Scan(fs, ct);
        foreach (var entry in entries)
        {
            await scan.AddRootAsync(entry).ConfigureAwait(false);
        }

        return new VirtualFileList(scan.Files, scan.Warning());
    }

    private sealed class Scan(RemoteFileSystem fs, CancellationToken ct)
    {
        public List<VirtualFile> Files { get; } = [];

        private bool _truncated;
        private int _skippedLinks;
        private int _skippedLong;
        private int _unreadable;

        public async Task AddRootAsync(RemoteEntry entry)
        {
            // Link escolhido de propósito: vale o destino (arquivo ou pasta).
            var (isDir, size, modified) = (entry.IsDirectory, entry.Size, entry.Modified);
            if (entry.IsSymlink)
            {
                if (await fs.StatAsync(entry.FullPath, ct).ConfigureAwait(false) is not { } target)
                {
                    _skippedLinks++;
                    return;
                }

                (isDir, size, modified) = target;
            }

            await AddAsync(entry.FullPath, LocalFiles.SafeName(entry.Name), isDir, size, modified, 0).ConfigureAwait(false);
        }

        private async Task AddAsync(string remote, string relative, bool isDirectory, long size, DateTime modified, int depth)
        {
            if (Files.Count >= MaxItems)
            {
                _truncated = true;
                return;
            }

            if (relative.Length > MaxPathChars)
            {
                _skippedLong++;
                return;
            }

            if (!isDirectory)
            {
                Files.Add(new VirtualFile(relative, false, size, modified, (output, token) => fs.CopyToAsync(remote, output, token)));
                return;
            }

            Files.Add(new VirtualFile(relative, true, 0, modified));
            if (depth >= MaxDepth)
            {
                _truncated = true;
                return;
            }

            IReadOnlyList<RemoteEntry> children;
            try
            {
                children = await fs.ListAsync(remote, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && depth > 0)
            {
                // Subpasta sem permissão (ou que sumiu): fica vazia e o resto é copiado.
                _unreadable++;
                return;
            }

            foreach (var child in children.OrderBy(c => c.IsDirectory ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var name = relative + "\\" + LocalFiles.SafeName(child.Name);
                if (child.IsSymlink)
                {
                    // Link para arquivo entra com o tamanho do destino; link para pasta fica de fora.
                    if (await fs.StatAsync(child.FullPath, ct).ConfigureAwait(false) is { IsDirectory: false } target)
                    {
                        await AddAsync(child.FullPath, name, false, target.Size, target.Modified, depth + 1).ConfigureAwait(false);
                    }
                    else
                    {
                        _skippedLinks++;
                    }

                    continue;
                }

                await AddAsync(child.FullPath, name, child.IsDirectory, child.Size, child.Modified, depth + 1).ConfigureAwait(false);
            }
        }

        public string? Warning()
        {
            var parts = new List<string>();
            if (_truncated)
            {
                parts.Add($"só os primeiros {MaxItems:N0} itens (ou {MaxDepth} níveis de pasta) foram copiados");
            }

            if (_skippedLinks > 0)
            {
                parts.Add($"{_skippedLinks} link(s) para pasta ou quebrado(s) ficaram de fora");
            }

            if (_unreadable > 0)
            {
                parts.Add($"{_unreadable} subpasta(s) sem permissão de leitura ficaram vazias");
            }

            if (_skippedLong > 0)
            {
                parts.Add($"{_skippedLong} item(ns) com caminho longo demais para o Windows ficaram de fora");
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}
