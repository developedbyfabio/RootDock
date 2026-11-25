using System.Text;

namespace RootDock.Core.Assistant;

/// <summary>
/// Anotações permanentes do assistente, um arquivo de texto por servidor na pasta de dados do
/// RootDock (o equivalente ao CLAUDE.md do Claude Code). Entram em toda conversa nova daquele
/// servidor; a pessoa edita pela tela e o assistente acrescenta com a ferramenta "remember".
/// </summary>
public sealed class AssistantMemoryStore(string directory)
{
    /// <summary>Teto do que entra na conversa (anotações demais custam em toda mensagem).</summary>
    public const int MaxChars = 24_000;

    private readonly object _gate = new();

    public string Read(string server)
    {
        lock (_gate)
        {
            try
            {
                var path = PathOf(server);
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    public void Write(string server, string text)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(PathOf(server), text.ReplaceLineEndings("\n").Trim() + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>Acrescenta uma anotação (uma linha de lista).</summary>
    public void Append(string server, string note)
    {
        lock (_gate)
        {
            var current = Read(server).TrimEnd();
            var line = "- " + note.ReplaceLineEndings(" ").Trim();
            Write(server, current.Length > 0 ? current + "\n" + line : line);
        }
    }

    private string PathOf(string server)
    {
        var name = new string(server.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return Path.Combine(directory, (name.Length > 0 ? name : "servidor") + ".md");
    }
}
