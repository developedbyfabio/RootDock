using RootDock.Core.Sftp;

namespace RootDock.Core.ServerTransfer;

/// <summary>
/// Listagem de uma pasta do servidor com as subpastas. Links simbólicos ficam de fora (um link
/// para pasta pareceria arquivo e a cópia falharia; seguir links poderia dar voltas sem fim).
/// </summary>
public sealed class RemoteTree
{
    public const int MaxFiles = 200_000;
    public const int MaxDepth = 48;

    /// <summary>Arquivos, pelo caminho relativo à raiz (com "/").</summary>
    public Dictionary<string, RemoteEntry> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Subpastas (caminho relativo), na ordem em que podem ser criadas.</summary>
    public List<(string Relative, RemoteEntry Entry)> Directories { get; } = [];

    public List<string> SkippedLinks { get; } = [];

    /// <summary>A listagem parou num limite: está incompleta.</summary>
    public bool Truncated { get; private set; }

    public static async Task<RemoteTree> ScanAsync(IFileTransport fs, string root, CancellationToken ct, string prefix = "")
    {
        var tree = new RemoteTree();
        await tree.CollectAsync(fs, root, prefix, 0, ct).ConfigureAwait(false);
        return tree;
    }

    private async Task CollectAsync(IFileTransport fs, string path, string relative, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth || Files.Count > MaxFiles)
        {
            Truncated = true;
            return;
        }

        foreach (var entry in await fs.ListAsync(path, ct).ConfigureAwait(false))
        {
            var rel = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.IsSymlink)
            {
                SkippedLinks.Add(rel);
            }
            else if (entry.IsDirectory)
            {
                Directories.Add((rel, entry));
                await CollectAsync(fs, entry.FullPath, rel, depth + 1, ct).ConfigureAwait(false);
            }
            else
            {
                Files[rel] = entry;
            }
        }
    }
}
