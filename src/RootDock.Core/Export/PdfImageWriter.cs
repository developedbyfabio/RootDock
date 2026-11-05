using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RootDock.Core.Export;

/// <summary>
/// PDF de uma página com uma imagem ocupando a página inteira (o "Exportar PDF" do print do
/// código). Imagem sem perdas (RGB comprimido com Flate) e, quando há transparência, uma máscara
/// de opacidade (SMask) para os cantos arredondados e a sombra ficarem certos.
/// </summary>
public static class PdfImageWriter
{
    /// <param name="rgb">Pixels RGB (3 bytes por pixel), linha a linha, de cima para baixo.</param>
    /// <param name="alpha">Opacidade (1 byte por pixel), ou null se a imagem é toda opaca.</param>
    /// <param name="pageWidth">Largura da página em pontos (1/72 pol.).</param>
    /// <param name="pageHeight">Altura da página em pontos.</param>
    public static void Write(Stream output, int width, int height, byte[] rgb, byte[]? alpha, double pageWidth, double pageHeight, string title)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("Tamanho dos pixels RGB não confere com a imagem.", nameof(rgb));
        }

        if (alpha is not null && alpha.Length != width * height)
        {
            throw new ArgumentException("Tamanho da máscara não confere com a imagem.", nameof(alpha));
        }

        var pdf = new Writer(output);
        var hasMask = alpha is not null;
        var w = Number(pageWidth);
        var h = Number(pageHeight);

        pdf.Begin();
        pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>");
        pdf.Stream(4, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode" +
                      (hasMask ? " /SMask 6 0 R" : ""), Deflate(rgb));
        pdf.Stream(5, "", Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n"));
        if (hasMask)
        {
            pdf.Stream(6, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Deflate(alpha!));
        }

        var info = hasMask ? 7 : 6;
        pdf.Object(info, $"<< /Title {Text(title)} /Producer (RootDock) /Creator (RootDock) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");
        pdf.End(rootObject: 1, infoObject: info);
    }

    /// <summary>Texto do PDF em UTF-16BE com BOM (acentos no título aparecem certos).</summary>
    private static string Text(string value)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        var hex = new StringBuilder("<FEFF", 6 + (bytes.Length * 2));
        foreach (var b in bytes)
        {
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return hex.Append('>').ToString();
    }

    private static string Number(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return buffer.ToArray();
    }

    /// <summary>Escreve os objetos guardando a posição de cada um para a tabela xref.</summary>
    private sealed class Writer(Stream output)
    {
        private readonly SortedDictionary<int, long> _offsets = new();
        private long _position;

        public void Begin()
        {
            Raw("%PDF-1.4\n");
            Raw([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // marca de arquivo binário
        }

        public void Object(int number, string body)
        {
            _offsets[number] = _position;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        public void Stream(int number, string dictionary, byte[] data)
        {
            _offsets[number] = _position;
            Raw($"{number} 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n");
            Raw(data);
            Raw("\nendstream\nendobj\n");
        }

        public void End(int rootObject, int infoObject)
        {
            var xref = _position;
            var count = _offsets.Keys.Max() + 1;
            var table = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
            for (var i = 1; i < count; i++)
            {
                table.Append(_offsets.TryGetValue(i, out var offset)
                    ? $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n"
                    : "0000000000 65535 f \n");
            }

            table.Append($"trailer\n<< /Size {count} /Root {rootObject} 0 R /Info {infoObject} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Raw(table.ToString());
            output.Flush();
        }

        private void Raw(string text) => Raw(Encoding.ASCII.GetBytes(text));

        private void Raw(byte[] data)
        {
            output.Write(data);
            _position += data.Length;
        }
    }
}
