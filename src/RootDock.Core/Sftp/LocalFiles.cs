namespace RootDock.Core.Sftp;

/// <summary>Regras para gravar arquivos baixados no computador sem perder nada.</summary>
public static class LocalFiles
{
    public const string PartialSuffix = ".rootdock-part";

    /// <summary>O caminho já está ocupado (arquivo, pasta ou download parcial em andamento).</summary>
    public static bool IsTaken(string path) =>
        File.Exists(path) || Directory.Exists(path) || File.Exists(path + PartialSuffix);

    /// <summary>"nome.ext", ou "nome (1).ext", "nome (2).ext"... o primeiro livre.</summary>
    public static string UniquePath(string path)
    {
        if (!IsTaken(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!IsTaken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Move o parcial terminado para o destino (sem sobrescrever, salvo pedido explícito).</summary>
    public static string Commit(string temp, string localPath, bool overwrite)
    {
        if (overwrite)
        {
            File.Move(temp, localPath, overwrite: true);
            return localPath;
        }

        for (var attempt = 0; ; attempt++)
        {
            var target = File.Exists(localPath) || Directory.Exists(localPath) ? UniqueFinal(localPath) : localPath;
            try
            {
                File.Move(temp, target, overwrite: false);
                return target;
            }
            catch (IOException) when (attempt < 5 && File.Exists(target))
            {
                // outro download terminou com o mesmo nome agora: tenta o próximo
            }
        }
    }

    private static string UniqueFinal(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Caminho local para um item remoto dentro de <paramref name="root"/>: cada parte do caminho
    /// relativo é limpa (sem "..", ":" ou "\" vindos do servidor) e o resultado tem de ficar dentro
    /// da pasta. Retorna null se escaparia dela.
    /// </summary>
    public static string? SafeCombine(string root, string relativeRemotePath)
    {
        var parts = relativeRemotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).ToArray();
        if (parts.Length == 0)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Nome de arquivo válido no Windows (caracteres proibidos viram "_"; "." e ".." não passam).</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 ? "arquivo" : safe;
    }
}
