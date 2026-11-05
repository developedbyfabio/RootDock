using System.IO;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;
using RootDock.Core.Text;

namespace RootDock.App.Remote;

/// <summary>
/// Editor próprio do RootDock (Monaco, o mesmo do VS Code) para arquivos do servidor: abre numa
/// aba, salva de volta pelo SFTP no próprio arquivo (dono e permissões preservados), avisa se o
/// arquivo mudou no servidor desde que foi aberto (com comparação lado a lado) e pede confirmação
/// em servidores de produção. Tudo na thread de interface.
/// </summary>
public sealed class EditorService
{
    /// <summary>Acima disso, sugere baixar ou abrir num programa externo.</summary>
    public const long MaxEditableBytes = 10 * 1024 * 1024;

    private readonly TerminalWorkspace _workspace;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, bool> _notify;
    private readonly Dictionary<string, Document> _documents = new();

    public EditorService(TerminalWorkspace workspace, Func<string, string, Task<bool>> confirm, Action<string, bool> notify)
    {
        _workspace = workspace;
        _confirm = confirm;
        _notify = notify;
    }

    private sealed class Document(string id, RemoteFilesViewModel files, string path, DecodedText text, DateTime modified, long size)
    {
        public string Id { get; } = id;
        public RemoteFilesViewModel Files { get; } = files;
        public string Path { get; } = path;
        public string Encoding { get; set; } = text.EncodingName;
        public bool Bom { get; set; } = text.HasBom;
        public DateTime Modified { get; set; } = modified;
        public long Size { get; set; } = size;
        public bool ProductionConfirmed { get; set; }
        public bool Saving { get; set; }
        public bool Dirty { get; set; }

        /// <summary>O terminal (e o SFTP) desta sessão foi fechado: o texto continua na aba, mas não salva mais no servidor.</summary>
        public bool Orphaned { get; set; }
    }

    /// <summary>Arquivos abertos com alterações não salvas (para avisar ao fechar o programa).</summary>
    public int DirtyCount => _documents.Values.Count(d => d.Dirty);

    /// <summary>Abre o arquivo numa aba de editor (ou mostra a aba se já estiver aberto).</summary>
    public async Task OpenAsync(RemoteFilesViewModel files, RemoteEntry entry)
    {
        if (_documents.Values.FirstOrDefault(d => d.Files == files && d.Path == entry.FullPath && !d.Orphaned) is { } open)
        {
            _workspace.Send(new { type = "focus", id = open.Id });
            _workspace.FocusTerminal();
            return;
        }

        if (entry.Size > MaxEditableBytes)
        {
            throw new IOException($"\"{entry.Name}\" tem {Format.Bytes(entry.Size)}; o editor abre arquivos de até {Format.Bytes(MaxEditableBytes)}. Use \"Abrir no programa padrão\" ou baixe o arquivo.");
        }

        var fs = await files.GetFileSystemAsync();
        var stat = await fs.GetAsync(entry.FullPath, CancellationToken.None);
        var bytes = await fs.ReadAllBytesAsync(entry.FullPath, MaxEditableBytes, CancellationToken.None);
        if (TextFile.LooksBinary(bytes))
        {
            throw new InvalidDataException($"\"{entry.Name}\" é um arquivo binário (imagem, executável, compactado...). Use \"Abrir no programa padrão\" ou baixe o arquivo.");
        }

        var text = TextFile.Decode(bytes);
        var id = Guid.NewGuid().ToString("N");
        _documents[id] = new Document(id, files, entry.FullPath, text, stat.Modified, stat.Size);
        var session = files.Session;
        _workspace.Send(new
        {
            type = "openEditor",
            id,
            name = entry.Name,
            path = entry.FullPath,
            host = Host(session),
            env = session.Environment.ToString(),
            content = text.Text,
            encoding = text.EncodingName,
            eol = text.LineEnding,
        });
        _workspace.FocusTerminal();
    }

    /// <summary>Terminal dono do arquivo aberto no editor (para a barra de status e a biblioteca de comandos).</summary>
    public TerminalSession? OwnerOf(string editorId, IEnumerable<TerminalSession> sessions) =>
        _documents.TryGetValue(editorId, out var document) && !document.Orphaned
            ? sessions.FirstOrDefault(s => s.Files == document.Files && !s.IsDisposed)
            : null;

    public bool IsEditor(string id) => _documents.ContainsKey(id);

    public async Task SaveAsync(string id, string text, bool force)
    {
        if (!_documents.TryGetValue(id, out var document) || document.Saving)
        {
            return;
        }

        if (document.Orphaned)
        {
            Reply(id, ok: false, message: "Não salvo: a sessão deste arquivo foi fechada. Salve uma cópia no computador.");
            return;
        }

        var session = document.Files.Session;
        if (session.Environment == SessionEnvironment.Production && !document.ProductionConfirmed)
        {
            if (!await _confirm("Salvar em PRODUÇÃO?", $"Gravar \"{document.Path}\" em {Host(session)}, um servidor de PRODUÇÃO?"))
            {
                Reply(id, ok: false, message: "Não salvo.");
                return;
            }

            document.ProductionConfirmed = true; // uma confirmação por arquivo aberto
        }

        document.Saving = true;
        try
        {
            var fs = await document.Files.GetFileSystemAsync();
            if (!force)
            {
                var current = await fs.GetAsync(document.Path, CancellationToken.None);
                if (current.Modified != document.Modified || current.Size != document.Size)
                {
                    _workspace.Send(new { type = "editorConflict", id, modified = current.Modified.ToLocalTime().ToString("dd/MM HH:mm:ss") });
                    return;
                }
            }

            byte[] content;
            try
            {
                content = TextFile.Encode(text, document.Encoding, document.Bom);
            }
            catch (System.Text.EncoderFallbackException)
            {
                Reply(id, ok: false, message: $"Não salvo: o texto tem caracteres que não existem na codificação {document.Encoding} do arquivo (ex.: €, aspas curvas ou emoji). Troque-os ou converta o arquivo para UTF-8 no servidor.");
                return;
            }

            // Grava no próprio arquivo (mantém dono, permissões e links), mas antes guarda a versão
            // atual: se a gravação falhar no meio (disco cheio, queda de rede), ela é restaurada.
            var original = await fs.ReadAllBytesAsync(document.Path, MaxEditableBytes, CancellationToken.None);
            var backup = SaveLocalBackup(document, original);
            try
            {
                await fs.WriteAllBytesAsync(document.Path, content, CancellationToken.None);
            }
            catch (Exception writeError) when (writeError is not OperationCanceledException)
            {
                Log.Error($"Salvar {document.Path}", writeError);
                string restored;
                try
                {
                    await fs.WriteAllBytesAsync(document.Path, original, CancellationToken.None);
                    restored = "O arquivo do servidor foi restaurado como estava.";
                }
                catch (Exception restoreError)
                {
                    Log.Error($"Restaurar {document.Path}", restoreError);
                    restored = backup is null
                        ? "ATENÇÃO: não foi possível restaurar o arquivo do servidor."
                        : $"ATENÇÃO: não foi possível restaurar o arquivo do servidor; a versão anterior está salva em {backup}.";
                }

                Reply(id, ok: false, message: $"Não salvo: {writeError.Message}. {restored} Seu texto continua aqui.");
                return;
            }

            DeleteLocalBackup(backup);
            var after = await fs.GetAsync(document.Path, CancellationToken.None);
            document.Modified = after.Modified;
            document.Size = after.Size;
            Reply(id, ok: true, message: $"Salvo às {DateTime.Now:HH:mm:ss}");
            if (RemotePath.Parent(document.Path) == document.Files.CurrentPath)
            {
                _ = document.Files.RefreshAsync(); // tamanho e data novos na lista
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Salvar {document.Path}", ex);
            Reply(id, ok: false, message: document.Files.IsSessionDisconnected
                ? "Não salvo: o terminal desta sessão está desconectado. Reconecte (Enter no terminal) e salve de novo; o texto continua aqui."
                : $"Não salvo: {ex.Message}");
        }
        finally
        {
            document.Saving = false;
        }
    }

    /// <summary>Versão atual do servidor, para comparar lado a lado ou recarregar.</summary>
    public async Task SendServerVersionAsync(string id, bool reload)
    {
        if (!_documents.TryGetValue(id, out var document))
        {
            return;
        }

        if (document.Orphaned)
        {
            Reply(id, ok: false, message: "A sessão deste arquivo foi fechada.");
            return;
        }

        try
        {
            var fs = await document.Files.GetFileSystemAsync();
            var bytes = await fs.ReadAllBytesAsync(document.Path, MaxEditableBytes, CancellationToken.None);
            var text = TextFile.Decode(bytes);
            if (reload)
            {
                var stat = await fs.GetAsync(document.Path, CancellationToken.None);
                document.Modified = stat.Modified;
                document.Size = stat.Size;
                document.Encoding = text.EncodingName;
                document.Bom = text.HasBom;
            }

            _workspace.Send(new { type = reload ? "editorReloaded" : "editorCompare", id, content = text.Text });
        }
        catch (Exception ex)
        {
            Log.Error($"Ler {document.Path}", ex);
            Reply(id, ok: false, message: $"Não foi possível ler do servidor: {ex.Message}");
        }
    }

    public void Close(string id) => _documents.Remove(id);

    public void SetDirty(string id, bool dirty)
    {
        if (_documents.TryGetValue(id, out var document))
        {
            document.Dirty = dirty;
        }
    }

    /// <summary>
    /// A sessão do arquivo foi fechada: as abas continuam abertas (o texto não se perde), mas
    /// salvar no servidor deixa de ser possível; a tela oferece salvar uma cópia no computador.
    /// </summary>
    public void Forget(RemoteFilesViewModel files)
    {
        foreach (var document in _documents.Values.Where(d => d.Files == files && !d.Orphaned))
        {
            document.Orphaned = true;
            _workspace.Send(new { type = "editorOrphaned", id = document.Id });
        }
    }

    /// <summary>Grava o texto do editor num arquivo do computador (mesma codificação do original).</summary>
    public void SaveLocalCopy(string id, string name, string text)
    {
        _documents.TryGetValue(id, out var document);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salvar uma cópia no computador",
            FileName = name,
            Filter = "Todos os arquivos (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var bytes = document is null ? TextFile.Encode(text, TextFile.Utf8, bom: false) : TextFile.Encode(text, document.Encoding, document.Bom);
            File.WriteAllBytes(dialog.FileName, bytes);
            _notify($"Cópia salva em {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Salvar cópia {dialog.FileName}", ex);
            _notify($"Não foi possível salvar a cópia: {ex.Message}", true);
        }
    }

    /// <summary>Cópia local da versão do servidor antes de sobrescrever (apagada se tudo der certo).</summary>
    private static string? SaveLocalBackup(Document document, byte[] original)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "RootDock", "editor-backup");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{RemoteFilesViewModel.SafeLocalName(RemotePath.Name(document.Path))}");
            File.WriteAllBytes(file, original);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Cópia de segurança do editor", ex);
            return null;
        }
    }

    private static void DeleteLocalBackup(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Apagar cópia do editor", ex);
        }
    }

    private void Reply(string id, bool ok, string message) => _workspace.Send(new { type = "editorSaved", id, ok, message });

    private static string Host(SessionEntry session) =>
        string.IsNullOrWhiteSpace(session.Username) ? session.Host : $"{session.Username}@{session.Host}";
}
