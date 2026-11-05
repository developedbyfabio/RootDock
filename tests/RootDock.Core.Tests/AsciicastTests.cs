using RootDock.Core.Recording;

namespace RootDock.Core.Tests;

public sealed class AsciicastTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-cast-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task RecordAndRead_RoundTrip()
    {
        var file = Path.Combine(_dir, "sessao.cast");
        using (var rec = new AsciicastRecorder(file, 120, 30, "web01"))
        {
            rec.Output("root@web01:~# ls\r\n");
            await Task.Delay(30, TestContext.Current.CancellationToken);
            rec.Output("ação \u001b[31mERRO\u001b[0m \"aspas\"\r\n");
            rec.Resize(100, 40);
        }

        var cast = Asciicast.Read(file);
        Assert.Equal(120, cast.Width);
        Assert.Equal(30, cast.Height);
        Assert.Equal("web01", cast.Title);
        Assert.Equal(3, cast.Events.Count);
        Assert.Equal("o", cast.Events[0].Kind);
        Assert.Equal("ação \u001b[31mERRO\u001b[0m \"aspas\"\r\n", cast.Events[1].Data);
        Assert.Equal(("r", "100x40"), (cast.Events[2].Kind, cast.Events[2].Data));
        Assert.True(cast.Events[1].Time >= cast.Events[0].Time);
        Assert.True(cast.Duration.TotalSeconds >= 0.02);

        var header = Asciicast.Read(file, headerOnly: true);
        Assert.Empty(header.Events);
        Assert.Equal(cast.Duration, header.Duration);
    }

    [Fact]
    public void TruncatedLastLine_IsIgnored()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "cortada.cast");
        File.WriteAllText(file, "{\"version\": 2, \"width\": 80, \"height\": 24}\n[0.5, \"o\", \"ok\"]\n[1.0, \"o\", \"cor");
        var cast = Asciicast.Read(file);
        Assert.Single(cast.Events);
    }
}
