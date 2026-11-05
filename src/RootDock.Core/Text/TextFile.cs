using System.Text;

namespace RootDock.Core.Text;

/// <summary>Texto lido de um arquivo, com o que é preciso para gravar de volta do mesmo jeito.</summary>
public sealed record DecodedText(string Text, string EncodingName, bool HasBom, string LineEnding);

/// <summary>
/// Abre e grava arquivos de texto preservando a codificação (UTF-8 com ou sem BOM, UTF-16,
/// Latin-1) e a quebra de linha (LF ou CRLF). Arquivos binários são recusados.
/// </summary>
public static class TextFile
{
    public const string Utf8 = "UTF-8";
    public const string Utf16Le = "UTF-16 LE";
    public const string Utf16Be = "UTF-16 BE";
    public const string Latin1 = "ISO-8859-1";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Tem byte nulo nos primeiros 8 KB (e não é UTF-16): é binário (imagem, executável, compactado).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        if (HasUtf16Bom(data))
        {
            return false;
        }

        return data[..Math.Min(data.Length, 8000)].IndexOf((byte)0) >= 0;
    }

    public static DecodedText Decode(byte[] bytes)
    {
        string text;
        string encoding;
        var bom = false;
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            encoding = Utf8;
            bom = true;
        }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            encoding = Utf16Le;
            bom = true;
        }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            encoding = Utf16Be;
            bom = true;
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                encoding = Utf8;
            }
            catch (DecoderFallbackException)
            {
                // Arquivos antigos (acentos em Latin-1): abre sem perder nenhum byte.
                text = Encoding.Latin1.GetString(bytes);
                encoding = Latin1;
            }
        }

        return new DecodedText(text, encoding, bom, DetectLineEnding(text));
    }

    public static byte[] Encode(string text, string encodingName, bool bom)
    {
        var (encoding, preamble) = encodingName switch
        {
            Utf16Le => ((Encoding)new UnicodeEncoding(false, false), new byte[] { 0xFF, 0xFE }),
            Utf16Be => (new UnicodeEncoding(true, false), new byte[] { 0xFE, 0xFF }),
            // Caractere fora do Latin-1 (€, emoji): erro em vez de virar "?" sem aviso.
            Latin1 => (Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback), Array.Empty<byte>()),
            _ => (new UTF8Encoding(false), new byte[] { 0xEF, 0xBB, 0xBF }),
        };
        var body = encoding.GetBytes(text);
        if (!bom || preamble.Length == 0)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>"CRLF" quando a maioria das quebras é \r\n (arquivos do Windows); senão "LF".</summary>
    public static string DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return crlf > lf ? "CRLF" : "LF";
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]);
}
