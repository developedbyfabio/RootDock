using System.Text.Json;

namespace RootDock.Core.Sessions;

/// <summary>Arquivo de sessões inválido. O arquivo original é preservado; nada é sobrescrito.</summary>
public sealed class SessionStoreCorruptedException(string path, Exception inner)
    : Exception($"O arquivo de sessões '{path}' está inválido e não foi alterado. Corrija-o ou restaure o backup '{path}.bak'.", inner)
{
    public string FilePath { get; } = path;
}

/// <summary>Lê e grava o arquivo de sessões (JSON) com escrita atômica e backup da versão anterior.</summary>
public sealed class SessionStore
{
    internal static readonly JsonSerializerOptions JsonOptions = Serialization.RootDockJson.Options;

    private readonly string _path;
    private readonly object _gate = new();

    public SessionStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public SessionDocument Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new SessionDocument();
            }

            try
            {
                using var stream = File.OpenRead(_path);
                var document = JsonSerializer.Deserialize<SessionDocument>(stream, JsonOptions) ?? new SessionDocument();
                Normalize(document);
                return document;
            }
            catch (JsonException ex)
            {
                throw new SessionStoreCorruptedException(_path, ex);
            }
        }
    }

    public void Save(SessionDocument document)
    {
        lock (_gate)
        {
            Normalize(document);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                // Troca atômica mantendo a versão anterior em .bak.
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }

    private static void Normalize(SessionDocument document)
    {
        document.Folders = document.Folders
            .Select(FolderPaths.Normalize)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var session in document.Sessions)
        {
            session.FolderPath = FolderPaths.Normalize(session.FolderPath);
        }
    }
}
