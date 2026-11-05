using System.Text;
using System.Text.RegularExpressions;
using RootDock.Core.Export;

namespace RootDock.Core.Tests;

/// <summary>PDF do "Exportar PDF" do print do código: estrutura válida e tabela xref certa.</summary>
public sealed class PdfImageWriterTests
{
    private static (byte[] Pdf, string Text) Make(bool withAlpha, string title = "Print do código")
    {
        const int w = 4;
        const int h = 3;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(i * 7);
        }

        var alpha = withAlpha ? Enumerable.Range(0, w * h).Select(i => (byte)(i * 20)).ToArray() : null;
        using var stream = new MemoryStream();
        PdfImageWriter.Write(stream, w, h, rgb, alpha, 300, 225.5, title);
        var bytes = stream.ToArray();
        return (bytes, Encoding.Latin1.GetString(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structure_IsValid_AndXrefPointsToObjects(bool withAlpha)
    {
        var (pdf, text) = Make(withAlpha);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/MediaBox [0 0 300 225.5]", text);
        Assert.Equal(withAlpha, text.Contains("/SMask 6 0 R"));

        // startxref aponta para a palavra "xref"
        var start = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF").Groups[1].Value);
        Assert.Equal("xref", text.Substring(start, 4));

        // cada entrada "n" da tabela aponta para "N 0 obj"
        var entries = Regex.Matches(text[start..], @"(\d{10}) 00000 n ");
        Assert.Equal(withAlpha ? 7 : 6, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Groups[1].Value);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..]);
        }

        Assert.True(pdf.Length > 300);
    }

    [Fact]
    public void StreamLength_MatchesTheData()
    {
        var (_, text) = Make(withAlpha: true);
        foreach (Match m in Regex.Matches(text, @"/Length (\d+) >>\nstream\n"))
        {
            var length = int.Parse(m.Groups[1].Value);
            var dataStart = m.Index + m.Length;
            Assert.Equal("\nendstream", text.Substring(dataStart + length, 10));
        }
    }

    [Fact]
    public void Title_WithAccents_IsEncodedAsUtf16()
    {
        var (_, text) = Make(withAlpha: false, title: "Configuração");
        Assert.Contains("/Title <FEFF0043006F006E00660069006700750072006100E700E3006F>", text);
    }

    [Fact]
    public void WrongPixelCount_IsRejected()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PdfImageWriter.Write(stream, 2, 2, new byte[5], null, 10, 10, "x"));
    }
}
