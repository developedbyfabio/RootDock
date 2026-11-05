using System.Text;
using RootDock.Core.Text;

namespace RootDock.Core.Tests;

public sealed class TextFileTests
{
    [Fact]
    public void Utf8WithoutBom_RoundTripsByteForByte()
    {
        var original = Encoding.UTF8.GetBytes("server {\n  server_name café.example;\n}\n");
        var text = TextFile.Decode(original);

        Assert.Equal(TextFile.Utf8, text.EncodingName);
        Assert.False(text.HasBom);
        Assert.Equal("LF", text.LineEnding);
        Assert.Equal(original, TextFile.Encode(text.Text, text.EncodingName, text.HasBom));
    }

    [Fact]
    public void Utf8WithBom_KeepsTheBom()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("ação\r\nlinha 2\r\n")).ToArray();
        var text = TextFile.Decode(original);

        Assert.True(text.HasBom);
        Assert.Equal("ação\r\nlinha 2\r\n", text.Text);
        Assert.Equal("CRLF", text.LineEnding);
        Assert.Equal(original, TextFile.Encode(text.Text, text.EncodingName, text.HasBom));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Utf16_IsTextAndRoundTrips(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        var original = encoding.GetPreamble().Concat(encoding.GetBytes("Get-Service | Where Status -eq 'Running'\r\n")).ToArray();

        Assert.False(TextFile.LooksBinary(original)); // UTF-16 tem bytes nulos, mas não é binário
        var text = TextFile.Decode(original);
        Assert.Equal(bigEndian ? TextFile.Utf16Be : TextFile.Utf16Le, text.EncodingName);
        Assert.Equal("Get-Service | Where Status -eq 'Running'\r\n", text.Text);
        Assert.Equal(original, TextFile.Encode(text.Text, text.EncodingName, text.HasBom));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1WithoutLosingBytes()
    {
        // "configuração" gravado em Latin-1 (arquivos antigos): não é UTF-8 válido.
        var original = Encoding.Latin1.GetBytes("# configuração\nvalor=ç\n");
        var text = TextFile.Decode(original);

        Assert.Equal(TextFile.Latin1, text.EncodingName);
        Assert.Contains("configuração", text.Text);
        Assert.Equal(original, TextFile.Encode(text.Text, text.EncodingName, text.HasBom));
    }

    [Fact]
    public void NullBytes_MeanBinary()
    {
        Assert.True(TextFile.LooksBinary(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 }));
        Assert.False(TextFile.LooksBinary(Encoding.UTF8.GetBytes("#!/bin/bash\necho ok\n")));
        Assert.False(TextFile.LooksBinary(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("a\nb\nc", "LF")]
    [InlineData("a\r\nb\r\nc", "CRLF")]
    [InlineData("a\r\nb\nc\nd", "LF")]
    [InlineData("sem quebra", "LF")]
    public void LineEnding_FollowsTheMajority(string text, string expected) =>
        Assert.Equal(expected, TextFile.DetectLineEnding(text));

    [Fact]
    public void EmptyFile_IsUtf8()
    {
        var text = TextFile.Decode(Array.Empty<byte>());
        Assert.Equal("", text.Text);
        Assert.Equal(TextFile.Utf8, text.EncodingName);
        Assert.Empty(TextFile.Encode("", text.EncodingName, text.HasBom));
    }
}
