using RootDock.Core.Sftp;
using RootDock.Core.Smb;

namespace RootDock.Core.Tests;

/// <summary>
/// Arquivos de servidor Windows (SMB) contra o próprio computador (\\localhost\C$), numa pasta
/// temporária. Pulado onde o compartilhamento administrativo não está acessível.
/// </summary>
public sealed class SmbFileSystemTests : IDisposable
{
    private readonly string _localDir = Path.Combine(Path.GetTempPath(), "rootdock-smb-" + Guid.NewGuid().ToString("N")[..8]);

    public SmbFileSystemTests() => Directory.CreateDirectory(_localDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_localDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool LocalAdminShare => Directory.Exists(@"\\localhost\C$\Windows");

    /// <summary>"C:\Users\x\Temp\pasta" → "/C$/Users/x/Temp/pasta".</summary>
    private static string Virtual(string localPath) =>
        "/" + char.ToUpperInvariant(localPath[0]) + "$/" + localPath[3..].Replace('\\', '/');

    [Theory]
    [InlineData(@"\\srv01\C$\inetpub\wwwroot", "/C$/inetpub/wwwroot")]
    [InlineData(@"\\SRV01\D$\", "/D$")]
    [InlineData(@"\\srv01", "/")]
    [InlineData(@"C:\Users\joao\Desktop", "/C$/Users/joao/Desktop")]
    [InlineData(@"d:", "/D$")]
    [InlineData(@"E:\", "/E$")]
    [InlineData("/C$/Windows/", "/C$/Windows")]
    [InlineData("/", "/")]
    [InlineData(@"\\outro\C$\x", null)]
    [InlineData("pasta solta", null)]
    public void WindowsPaths_BecomeManagerPaths(string typed, string? expected) =>
        Assert.Equal(expected, SmbFileSystem.FromWindowsPath("srv01", typed));

    [Fact]
    public async Task Root_ListsDrivesFirst_AndHidesAdminShares()
    {
        Assert.SkipUnless(LocalAdminShare, "Compartilhamento administrativo \\\\localhost\\C$ indisponível.");
        using var fs = await SmbFileSystem.ConnectAsync("localhost", null, null, TestContext.Current.CancellationToken);

        var shares = await fs.ListAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("C$", shares[0].Name);
        Assert.Equal("/C$", shares[0].FullPath);
        Assert.Contains("Unidade C:", shares[0].Permissions);
        Assert.All(shares, s => Assert.True(s.IsDirectory));
        Assert.DoesNotContain(shares, s => s.Name is "ADMIN$" or "IPC$" or "print$");
    }

    [Fact]
    public async Task RoundTrip_CreateUploadListDownloadRenameDelete()
    {
        Assert.SkipUnless(LocalAdminShare, "Compartilhamento administrativo \\\\localhost\\C$ indisponível.");
        var ct = TestContext.Current.CancellationToken;
        using var fs = await SmbFileSystem.ConnectAsync("localhost", null, null, ct);
        var remoteDir = Virtual(Path.Combine(_localDir, "servidor"));
        var source = Path.Combine(_localDir, "nota.txt");
        await File.WriteAllTextAsync(source, "conteúdo de teste", ct);

        await fs.CreateDirectoryAsync(remoteDir, ct);
        await fs.UploadFileAsync(source, RemotePath.Combine(remoteDir, "nota.txt"), null, ct);

        var listed = await fs.ListAsync(remoteDir, ct);
        var entry = Assert.Single(listed);
        Assert.Equal("nota.txt", entry.Name);
        Assert.Equal(new FileInfo(source).Length, entry.Size);
        Assert.False(entry.IsDirectory);
        Assert.DoesNotContain(listed, e => e.Name.EndsWith(LocalFiles.PartialSuffix, StringComparison.Ordinal));

        var downloaded = await fs.DownloadFileAsync(entry.FullPath, Path.Combine(_localDir, "baixado", "nota.txt"), null, ct);
        Assert.Equal("conteúdo de teste", await File.ReadAllTextAsync(downloaded, ct));

        // Baixar de novo não apaga o arquivo que já existe no computador.
        var second = await fs.DownloadFileAsync(entry.FullPath, downloaded, null, ct);
        Assert.NotEqual(downloaded, second);

        var renamed = RemotePath.Combine(remoteDir, "nota-renomeada.txt");
        await fs.RenameAsync(entry.FullPath, renamed, ct);
        Assert.False(await fs.ExistsAsync(entry.FullPath, ct));
        Assert.True(await fs.ExistsAsync(renamed, ct));

        await fs.DeleteAsync(await fs.GetAsync(remoteDir, ct), ct);
        Assert.False(Directory.Exists(Path.Combine(_localDir, "servidor")));
    }

    [Fact]
    public async Task ReadOnlyFiles_AreDeletedWithTheirFolder()
    {
        Assert.SkipUnless(LocalAdminShare, "Compartilhamento administrativo \\\\localhost\\C$ indisponível.");
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateDirectory(Path.Combine(_localDir, "pasta", "sub"));
        var file = Path.Combine(folder.FullName, "travado.txt");
        await File.WriteAllTextAsync(file, "x", ct);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        using var fs = await SmbFileSystem.ConnectAsync("localhost", null, null, ct);

        await fs.DeleteAsync(await fs.GetAsync(Virtual(Path.Combine(_localDir, "pasta")), ct), ct);

        Assert.False(Directory.Exists(Path.Combine(_localDir, "pasta")));
    }

    [Theory]
    [InlineData("/C$/../../outro")]
    [InlineData("/C$/pasta/./x")]
    public async Task ParentSegments_AreRejected(string path)
    {
        Assert.SkipUnless(LocalAdminShare, "Compartilhamento administrativo \\\\localhost\\C$ indisponível.");
        using var fs = await SmbFileSystem.ConnectAsync("localhost", null, null, TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => fs.ToUnc(path));
    }

    [Fact]
    public async Task Shares_CannotBeDeletedOrRenamed()
    {
        Assert.SkipUnless(LocalAdminShare, "Compartilhamento administrativo \\\\localhost\\C$ indisponível.");
        var ct = TestContext.Current.CancellationToken;
        using var fs = await SmbFileSystem.ConnectAsync("localhost", null, null, ct);

        // Compartilhamento inexistente de propósito: mesmo se a proteção falhasse, nada seria apagado.
        var share = new RemoteEntry("NaoExiste$", "/NaoExiste$", true, false, 0, default, "", 0, 0);

        await Assert.ThrowsAsync<IOException>(() => fs.DeleteAsync(share, ct));
        await Assert.ThrowsAsync<IOException>(() => fs.RenameAsync("/NaoExiste$", "/Outro$", ct));
        await Assert.ThrowsAsync<IOException>(() => fs.CreateDirectoryAsync("/nova", ct));
    }
}
