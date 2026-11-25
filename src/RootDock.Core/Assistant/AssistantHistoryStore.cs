using System.Text;
using System.Text.Json;

namespace RootDock.Core.Assistant;

/// <summary>Uma conversa guardada: de qual servidor é, o título e tudo o que apareceu na tela.</summary>
public sealed class AssistantHistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Servidor da conversa ("host:porta"): o histórico de um servidor não se mistura com o de outro.</summary>
    public string Server { get; set; } = "";

    public string ServerName { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime Updated { get; set; } = DateTime.UtcNow;

    /// <summary>Os eventos da tela (mensagens, comandos, resultados), na ordem.</summary>
    public List<JsonElement> Events { get; set; } = [];
}

public sealed record AssistantHistoryItem(string Id, string Title, DateTime Updated);

/// <summary>
/// Histórico das conversas do assistente, um arquivo por conversa na pasta de dados do RootDock.
/// Nada é apagado por conta própria; a pessoa reabre uma conversa antiga pelo botão de histórico.
/// </summary>
public sealed class AssistantHistoryStore(string directory)
{
    public const string TrashFolder = "lixeira";

    private const int TranscriptChars = 40_000;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Save(AssistantHistoryRecord record)
    {
        Directory.CreateDirectory(directory);
        var path = PathOf(record.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true); // nunca deixa um arquivo pela metade no lugar do bom
    }

    /// <summary>As conversas de um servidor, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<AssistantHistoryItem> List(string server)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var items = new List<AssistantHistoryItem>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (ReadHeader(file) is { } header && header.Server == server && header.Id.Length > 0)
                {
                    items.Add(new AssistantHistoryItem(header.Id, header.Title, header.Updated ?? File.GetLastWriteTimeUtc(file)));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // arquivo ilegível: fica fora da lista, sem derrubar o histórico inteiro
            }
        }

        return items.OrderByDescending(i => i.Updated).ToList();
    }

    /// <summary>
    /// Tira a conversa do histórico sem destruí-la: o arquivo vai para a subpasta "lixeira", de
    /// onde a pessoa pode trazê-lo de volta à mão.
    /// </summary>
    public bool Delete(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            return false;
        }

        var trash = Path.Combine(directory, TrashFolder);
        Directory.CreateDirectory(trash);
        File.Move(path, Path.Combine(trash, id + ".json"), overwrite: true);
        return true;
    }

    /// <summary>
    /// Só o cabeçalho da conversa (os campos que vêm antes dos eventos), lendo o começo do arquivo:
    /// a lista do histórico não pode custar a leitura de todas as conversas inteiras.
    /// </summary>
    private static (string Id, string Server, string Title, DateTime? Updated)? ReadHeader(string file)
    {
        var buffer = new byte[16 * 1024];
        int length;
        using (var stream = File.OpenRead(file))
        {
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        var reader = new Utf8JsonReader(buffer.AsSpan(0, length), isFinalBlock: false, state: default);
        string id = "", server = "", title = "";
        DateTime? updated = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            if (name == "events" || !reader.Read())
            {
                break; // daqui em diante é a conversa em si
            }

            switch (name)
            {
                case "id": id = reader.GetString() ?? ""; break;
                case "server": server = reader.GetString() ?? ""; break;
                case "title": title = reader.GetString() ?? ""; break;
                case "updated" when reader.TryGetDateTime(out var when): updated = when; break;
                default:
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                    {
                        return (id, server, title, updated);
                    }

                    break;
            }
        }

        return (id, server, title, updated);
    }

    public AssistantHistoryRecord? Load(string id)
    {
        try
        {
            var path = PathOf(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<AssistantHistoryRecord>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A conversa em texto corrido, para o modelo retomar de onde parou (qualquer provedor entende,
    /// mesmo que a conversa tenha sido com outro). Conversa longa: fica o fim.
    /// </summary>
    public static string Transcript(IEnumerable<JsonElement> events)
    {
        var text = new StringBuilder();
        var speaking = false;
        foreach (var item in events)
        {
            var type = Text(item, "type");
            if (type == "delta")
            {
                text.Append(speaking ? "" : "ASSISTENTE: ").Append(Text(item, "text"));
                speaking = true;
                continue;
            }

            if (speaking)
            {
                text.Append('\n');
                speaking = false;
            }

            switch (type)
            {
                case "user":
                    text.Append("PESSOA: ").Append(Text(item, "text"))
                        .Append(item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? " [imagem anexada; não está mais disponível]" : "")
                        .Append('\n');
                    break;

                case "tool":
                    text.Append("FERRAMENTA ").Append(Text(item, "name")).Append(": ").Append(Text(item, "summary")).Append('\n');
                    break;

                case "toolEnd":
                    var output = Text(item, "output") ?? "";
                    text.Append("RESULTADO: ").Append(output.Length > 1500 ? output[..1500] + " [...]" : output).Append('\n');
                    break;
            }
        }

        var all = text.ToString().TrimEnd();
        return all.Length <= TranscriptChars ? all : "[... começo omitido ...]\n" + all[^TranscriptChars..];
    }

    private string PathOf(string id) =>
        id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit)
            ? Path.Combine(directory, id + ".json")
            : throw new ArgumentException("Identificador de conversa inválido.", nameof(id));

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
