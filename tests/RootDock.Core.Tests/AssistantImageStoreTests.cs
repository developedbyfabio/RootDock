using RootDock.Core.Assistant;
using Xunit;

namespace RootDock.Core.Tests;

public class AssistantImageStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rootdock-images-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Save_ThenLoad_ReturnsTheSameBytes_AndTheSameImageIsStoredOnce()
    {
        var store = new AssistantImageStore(_folder);
        byte[] bytes = [1, 2, 3, 250, 0, 9];
        var id = store.Save(new ChatImage("image/jpeg", Convert.ToBase64String(bytes)))!;
        Assert.Equal(32, id.Length);
        Assert.Equal(bytes, store.Load(id));
        Assert.Equal(id, store.Save(new ChatImage("image/jpeg", Convert.ToBase64String(bytes))));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..\\..\\segredo")]
    [InlineData("0af44bf9807e8422cc4428e0fb348dbZ")]
    [InlineData("0AF44BF9807E8422CC4428E0FB348DBE")]
    [InlineData("0af44bf9807e8422cc4428e0fb348dbe")]
    public void Load_UnknownOrMalformedId_ReturnsNull(string? id) =>
        Assert.Null(new AssistantImageStore(_folder).Load(id));

    [Fact]
    public void Save_InvalidBase64_ReturnsNull() =>
        Assert.Null(new AssistantImageStore(_folder).Save(new ChatImage("image/jpeg", "isto não é base64")));

    [Fact]
    public void Save_RemovesImagesOlderThanTheLimit()
    {
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, new string('a', 32) + ".img");
        File.WriteAllBytes(old, [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - AssistantImageStore.KeepFor - TimeSpan.FromDays(1));
        var recent = Path.Combine(_folder, new string('b', 32) + ".img");
        File.WriteAllBytes(recent, [2]);

        new AssistantImageStore(_folder).Save(new ChatImage("image/png", Convert.ToBase64String([7, 7])));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
