using System.Text;

namespace RootDock.Core.MultiExec;

/// <summary>
/// Texto com teto de memória: guarda o começo e o fim e conta o que ficou no meio. Para saídas de
/// comandos que podem ter gigabytes (journalctl, cat de log).
/// </summary>
internal sealed class BoundedText(int headChars, int tailChars)
{
    private readonly StringBuilder _head = new();
    private readonly char[] _tail = new char[tailChars];
    private int _tailStart;
    private int _tailCount;
    private long _dropped;

    public void Append(ReadOnlySpan<char> text)
    {
        var free = headChars - _head.Length;
        if (free > 0)
        {
            var take = Math.Min(free, text.Length);
            _head.Append(text[..take]);
            text = text[take..];
        }

        foreach (var c in text)
        {
            if (_tail.Length == 0)
            {
                _dropped++;
                continue;
            }

            if (_tailCount == _tail.Length)
            {
                _tailStart = (_tailStart + 1) % _tail.Length; // o mais antigo do fim sai
                _dropped++;
            }
            else
            {
                _tailCount++;
            }

            _tail[(_tailStart + _tailCount - 1) % _tail.Length] = c;
        }
    }

    public override string ToString()
    {
        var result = new StringBuilder(_head.Length + _tailCount + 80);
        result.Append(_head);
        if (_dropped > 0)
        {
            result.Append($"\n[... {_dropped:N0} caracteres omitidos (saída grande demais) ...]\n");
        }

        for (var i = 0; i < _tailCount; i++)
        {
            result.Append(_tail[(_tailStart + i) % _tail.Length]);
        }

        return result.ToString();
    }

    /// <summary>Lê o fluxo até o fim guardando no máximo começo + fim.</summary>
    public static async Task<string> ReadAsync(Stream stream, int headChars, int tailChars, CancellationToken ct)
    {
        var text = new BoundedText(headChars, tailChars);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer.AsSpan(0, read));
        }

        return text.ToString();
    }
}
