using System.Text;
using RootDock.Core.History;
using RootDock.Core.MultiExec;
using RootDock.Core.Sftp;
using RootDock.Core.Text;

namespace RootDock.Core.Tests;

/// <summary>Casos apontados na revisão de código (path traversal, sobrescrita, segredos, trava de rm).</summary>
public sealed class ReviewFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-review-" + Guid.NewGuid().ToString("N"));

    public ReviewFixesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("..\\..\\AppData\\Roaming\\Startup\\x.bat")]
    [InlineData("../../fora.txt")]
    [InlineData("C:/Windows/System32/x.dll")]
    [InlineData("pasta/..\\..\\x")]
    public void SafeCombine_NeverLeavesTheRoot(string remoteRelative)
    {
        var result = LocalFiles.SafeCombine(_dir, remoteRelative);
        if (result is not null)
        {
            Assert.StartsWith(Path.GetFullPath(_dir) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SafeCombine_KeepsNormalPaths()
    {
        Assert.Equal(Path.Combine(_dir, "www", "app", "index.php"), LocalFiles.SafeCombine(_dir, "www/app/index.php"));
        Assert.Equal(Path.Combine(_dir, "a_b.txt"), LocalFiles.SafeCombine(_dir, "a:b.txt"));
    }

    [Fact]
    public void Commit_DoesNotOverwrite_UnlessAsked()
    {
        var target = Path.Combine(_dir, "error.log");
        File.WriteAllText(target, "do usuário");
        File.WriteAllText(target + LocalFiles.PartialSuffix, "baixado");

        var saved = LocalFiles.Commit(target + LocalFiles.PartialSuffix, target, overwrite: false);

        Assert.Equal(Path.Combine(_dir, "error (1).log"), saved);
        Assert.Equal("do usuário", File.ReadAllText(target));
        Assert.Equal("baixado", File.ReadAllText(saved));

        File.WriteAllText(target + LocalFiles.PartialSuffix, "novo");
        Assert.Equal(target, LocalFiles.Commit(target + LocalFiles.PartialSuffix, target, overwrite: true));
        Assert.Equal("novo", File.ReadAllText(target));
    }

    [Fact]
    public void UniquePath_ConsidersDownloadsInProgress()
    {
        var target = Path.Combine(_dir, "backup.tar.gz");
        File.WriteAllText(target + LocalFiles.PartialSuffix, "parcial");
        Assert.Equal(Path.Combine(_dir, "backup.tar (1).gz"), LocalFiles.UniquePath(target));
    }

    [Fact]
    public void Latin1_RefusesCharactersItCannotStore()
    {
        Assert.Throws<EncoderFallbackException>(() => TextFile.Encode("preço 10 €", TextFile.Latin1, bom: false));
        Assert.Equal(Encoding.Latin1.GetBytes("preço"), TextFile.Encode("preço", TextFile.Latin1, bom: false));
    }

    [Theory]
    [InlineData("curl -u admin:S3gredo https://api", "S3gredo")]
    [InlineData("docker login -p tokenXYZ registry.local", "tokenXYZ")]
    [InlineData("redis-cli -h db -a SenhaRedis ping", "SenhaRedis")]
    [InlineData("htpasswd -b /etc/nginx/.htpasswd joao SenhaHt", "SenhaHt")]
    [InlineData("echo 'joao:Nova123' | chpasswd", "Nova123")]
    [InlineData("psql -c \"ALTER ROLE app WITH PASSWORD 'Pg123'\"", "Pg123")]
    [InlineData("mysql -e \"SET PASSWORD = 'My456'\"", "My456")]
    [InlineData("git clone https://joao:a/b@c@gitlab.local/x.git", "a/b@c")]
    public void Sanitizer_CoversMoreSecrets(string command, string secret)
    {
        var clean = HistorySanitizer.Clean(command)!;
        Assert.DoesNotContain(secret, clean);
        Assert.Contains(HistorySanitizer.Mask, clean);
    }

    [Theory]
    [InlineData("rm -f -r /data")]
    [InlineData("rm -v -rf /srv/app")]
    [InlineData("rm --force --recursive build")]
    [InlineData("sudo rm -R /opt/x")]
    public void RmVariants_AreFlagged(string command) => Assert.NotEmpty(CommandSafety.Analyze(command));

    [Fact]
    public void HistorySearch_FiltersBeforeRemovingRepeats()
    {
        var history = new CommandHistory(Path.Combine(_dir, "h.jsonl"));
        var prod = Guid.NewGuid();
        var dev = Guid.NewGuid();
        var t = DateTimeOffset.Now;
        history.Add(prod, "srv-prod", "10.0.0.1", "ls", t.AddMinutes(-2));
        history.Add(dev, "srv-dev", "10.0.0.2", "ls", t);

        var found = history.Search("ls prod");

        Assert.Single(found);
        Assert.Equal("srv-prod", found[0].SessionName);
    }
}
