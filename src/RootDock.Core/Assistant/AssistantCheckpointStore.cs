using System.Text;
using System.Text.Json;

namespace RootDock.Core.Assistant;

/// <summary>Como um arquivo estava antes de o assistente mexer nele. <c>Before</c> nulo = o arquivo não existia.</summary>
public sealed record FileCheckpoint(string Path, byte[]? Before);

/// <summary>
/// O estado dos arquivos antes de cada resposta do assistente, para a pessoa poder desfazer.
/// Fica na pasta de dados do RootDock, um arquivo por resposta, dentro da pasta da conversa.
/// Só cobre o que passou por write_file e edit_file; o que um comando de shell alterou não tem volta aqui.
/// </summary>
public sealed class AssistantCheckpointStore(string directory)
{
    public void Save(string conversation, string turn, IReadOnlyList<FileCheckpoint> files)
    {
        var folder = FolderOf(conversation);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Safe(turn) + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(files), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public IReadOnlyList<FileCheckpoint>? Load(string conversation, string turn)
    {
        try
        {
            var path = Path.Combine(FolderOf(conversation), Safe(turn) + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<List<FileCheckpoint>>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A conversa foi para a lixeira: os pontos de retorno dela vão junto.</summary>
    public void MoveToTrash(string conversation)
    {
        var folder = FolderOf(conversation);
        if (!Directory.Exists(folder))
        {
            return;
        }

        var trash = Path.Combine(directory, AssistantHistoryStore.TrashFolder);
        Directory.CreateDirectory(trash);
        var target = Path.Combine(trash, Safe(conversation));
        if (!Directory.Exists(target))
        {
            Directory.Move(folder, target);
        }
    }

    private string FolderOf(string conversation) => Path.Combine(directory, Safe(conversation));

    private static string Safe(string id) =>
        id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit) ? id : throw new ArgumentException("Identificador inválido.", nameof(id));
}
