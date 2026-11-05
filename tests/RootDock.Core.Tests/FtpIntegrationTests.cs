using RootDock.Core.Ftp;

namespace RootDock.Core.Tests;

/// <summary>
/// Contra um servidor FTP real (só roda com ROOTDOCK_FTP_TEST=1 e um servidor em 127.0.0.1:2121,
/// usuário teste / senha123 — ex.: pyftpdlib). Fora disso é ignorado.
/// </summary>
public sealed class FtpIntegrationTests
{
    [Fact]
    public async Task ListUploadDownloadRenameDelete()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ROOTDOCK_FTP_TEST") == "1", "Servidor FTP de teste não configurado.");
        var ct = TestContext.Current.CancellationToken;
        using var fs = await FtpFileSystem.ConnectAsync("127.0.0.1", 2121, "teste", "senha123", new FtpSettings { Security = FtpSecurity.None }, ct);
        Assert.Equal("FTP", fs.Protocol);

        var dir = Path.Combine(Path.GetTempPath(), "rootdock-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var local = Path.Combine(dir, "envio.txt");
            await File.WriteAllTextAsync(local, string.Concat(Enumerable.Repeat("linha ação\n", 5000)), ct);
            var remote = $"{fs.HomeDirectory.TrimEnd('/')}/rootdock-teste/envio.txt";
            long reported = 0;
            await fs.UploadFileAsync(local, remote, new Progress<long>(b => reported = b), ct);

            var listing = await fs.ListAsync($"{fs.HomeDirectory.TrimEnd('/')}/rootdock-teste", ct);
            var entry = Assert.Single(listing);
            Assert.Equal("envio.txt", entry.Name);
            Assert.Equal(new FileInfo(local).Length, entry.Size);
            Assert.True(await fs.ExistsAsync(remote, ct));

            var back = Path.Combine(dir, "volta.txt");
            await fs.DownloadFileAsync(remote, back, null, ct);
            Assert.Equal(await File.ReadAllBytesAsync(local, ct), await File.ReadAllBytesAsync(back, ct));

            var renamed = remote.Replace("envio.txt", "renomeado.txt");
            await fs.RenameAsync(remote, renamed, ct);
            Assert.False(await fs.ExistsAsync(remote, ct));
            var info = await fs.GetAsync(renamed, ct);
            Assert.Equal(entry.Size, info.Size);

            await fs.DeleteAsync(info, ct);
            Assert.False(await fs.ExistsAsync(renamed, ct));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPassword_IsExplained()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ROOTDOCK_FTP_TEST") == "1", "Servidor FTP de teste não configurado.");
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            FtpFileSystem.ConnectAsync("127.0.0.1", 2121, "teste", "errada", new FtpSettings { Security = FtpSecurity.None }, TestContext.Current.CancellationToken));
        Assert.Contains("recusados", ex.Message);
    }
}
