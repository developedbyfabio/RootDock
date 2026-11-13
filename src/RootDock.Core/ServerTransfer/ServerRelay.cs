using RootDock.Core.Sftp;

namespace RootDock.Core.ServerTransfer;

/// <summary>
/// Cópia entre dois servidores passando pelo RootDock (SFTP dos dois lados, sem gravar neste
/// computador). Funciona sempre que os dois estão conectados; a velocidade depende da rede daqui
/// até cada um. Guarda o que já terminou, então retomar não refaz nem pula arquivo errado.
/// </summary>
public sealed class ServerRelay(RemoteFileSystem source, RemoteFileSystem target, CopyRequest request)
{
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private List<(string Relative, RemoteEntry Entry)>? _files;
    private List<(string Relative, RemoteEntry Entry)> _directories = [];
    private int _skippedLinks;
    private bool _truncated;

    /// <summary>Roda (ou continua) a cópia. <paramref name="resume"/>: aproveita o parcial deixado numa pausa.</summary>
    public async Task<RelaySummary> RunAsync(
        bool resume, Action<long> setTotal, Action<long> setDone, Func<bool> pauseRequested, CancellationToken ct)
    {
        if (_files is null)
        {
            await ExpandAsync(ct).ConfigureAwait(false);
        }

        var files = _files!;
        if (_truncated)
        {
            throw new IOException($"A pasta tem mais de {RemoteTree.MaxFiles:N0} arquivos ou subpastas demais: copie em partes ou use o modo direto (rsync).");
        }

        setTotal(files.Sum(f => f.Entry.Size));
        await EnsureDirectoryAsync(request.TargetRoot, ct).ConfigureAwait(false);
        foreach (var (relative, _) in _directories)
        {
            await EnsureDirectoryAsync(RemotePath.Combine(request.TargetRoot, relative), ct).ConfigureAwait(false);
        }

        long baseline = 0;
        var copied = 0;
        var skipped = 0;
        foreach (var (relative, entry) in files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = RemotePath.Combine(request.TargetRoot, relative);
            if (_finished.Contains(relative))
            {
                baseline += entry.Size;
                setDone(baseline);
                continue;
            }

            if (request.SkipExisting && await target.ExistsAsync(destination, ct).ConfigureAwait(false))
            {
                skipped++;
                _finished.Add(relative);
                baseline += entry.Size;
                setDone(baseline);
                continue;
            }

            var parent = RemotePath.Parent(destination);
            if (parent != request.TargetRoot)
            {
                await EnsureDirectoryAsync(parent, ct).ConfigureAwait(false);
            }

            var start = baseline;
            await source.CopyFileToAsync(target, entry.FullPath, destination, request.Preserve, resume,
                new Reporter(bytes => setDone(start + bytes)), ct, pauseRequested).ConfigureAwait(false);
            _finished.Add(relative);
            copied++;
            baseline += entry.Size;
            setDone(baseline);
        }

        if (request.Preserve)
        {
            // Datas das pastas por último (criar arquivos dentro delas mexe na data), das mais fundas para as de cima.
            foreach (var (relative, entry) in _directories.AsEnumerable().Reverse())
            {
                try
                {
                    await target.SetModifiedAsync(RemotePath.Combine(request.TargetRoot, relative), entry.Modified, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A data da pasta é detalhe: os arquivos já estão completos.
                }
            }
        }

        return new RelaySummary(copied, skipped, _skippedLinks);
    }

    /// <summary>Lista o que os itens pedidos contêm (pastas com tudo dentro).</summary>
    private async Task ExpandAsync(CancellationToken ct)
    {
        var files = new List<(string, RemoteEntry)>();
        var directories = new List<(string, RemoteEntry)>();
        foreach (var item in request.Items)
        {
            var path = RemotePath.Combine(request.SourceRoot, item.RelativePath);
            var entry = await source.GetAsync(path, ct).ConfigureAwait(false);
            if (entry.IsSymlink)
            {
                _skippedLinks++;
            }
            else if (entry.IsDirectory)
            {
                directories.Add((item.RelativePath, entry));
                var tree = await RemoteTree.ScanAsync(source, path, ct, item.RelativePath).ConfigureAwait(false);
                directories.AddRange(tree.Directories);
                files.AddRange(tree.Files.Select(f => (f.Key, f.Value)));
                _skippedLinks += tree.SkippedLinks.Count;
                _truncated |= tree.Truncated;
            }
            else
            {
                files.Add((item.RelativePath, entry));
            }
        }

        _directories = directories;
        _files = files;
    }

    private async Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        if (path is "/" or "" || await target.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return;
        }

        await EnsureDirectoryAsync(RemotePath.Parent(path), ct).ConfigureAwait(false);
        await target.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    private sealed class Reporter(Action<long> handler) : IProgress<long>
    {
        public void Report(long value) => handler(value);
    }
}
