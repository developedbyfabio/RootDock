using System.Security.Cryptography;

namespace RootDock.Core.Assistant;

/// <summary>
/// As imagens anexadas às conversas, em tamanho inteiro, na pasta de dados do RootDock. A página
/// e o histórico guardam só a miniatura; é daqui que sai a imagem grande quando a pessoa clica
/// nela. O nome do arquivo é o resumo do conteúdo, então a mesma imagem não ocupa espaço duas vezes.
/// </summary>
public sealed class AssistantImageStore(string directory)
{
    /// <summary>Imagens mais velhas que isto são apagadas (a miniatura continua na conversa).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(120);

    private bool _pruned;

    /// <summary>Guarda a imagem e devolve o id dela; null se não deu (a conversa segue com a miniatura).</summary>
    public string? Save(ChatImage image)
    {
        try
        {
            var bytes = Convert.FromBase64String(image.Base64);
            var id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];
            Directory.CreateDirectory(directory);
            Prune();
            var path = PathOf(id);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // em uso de novo: não é velha
            }

            return id;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? Load(string? id)
    {
        if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigitLower))
        {
            return null; // o id vem da página: só o formato que este armazenamento gera
        }

        try
        {
            var path = PathOf(id);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string PathOf(string id) => Path.Combine(directory, id + ".img");

    private void Prune()
    {
        if (_pruned)
        {
            return;
        }

        _pruned = true;
        var limit = DateTime.UtcNow - KeepFor;
        foreach (var file in Directory.EnumerateFiles(directory, "*.img"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // fica para a próxima
            }
        }
    }
}
