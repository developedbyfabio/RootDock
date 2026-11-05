using RootDock.Core.History;

namespace RootDock.Core.Tests;

public sealed class CommandHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("mysql -uroot -pS3nh@Forte -e 'show databases'", "S3nh@Forte")]
    [InlineData("mysqldump -u app -p'minha senha' loja > loja.sql", "minha senha")]
    [InlineData("curl -H 'Authorization: Bearer eyJhbGciOi.abc.def' https://api", "eyJhbGciOi.abc.def")]
    [InlineData("git clone https://joao:ghp_TOKEN123@github.com/org/repo.git", "ghp_TOKEN123")]
    [InlineData("PGPASSWORD=segredo psql -h db -U app", "segredo")]
    [InlineData("export DB_PASSWORD=\"abc 123\"", "abc 123")]
    [InlineData("docker login --password hunter2 registry", "hunter2")]
    [InlineData("app --token=xyz987 run", "xyz987")]
    [InlineData("mysql -e \"CREATE USER 'a'@'%' IDENTIFIED BY 'Pa55'\"", "Pa55")]
    [InlineData("sshpass -p 'Senha1' ssh root@host", "Senha1")]
    [InlineData("echo 'Senha1' | sudo -S apt update", "Senha1")]
    public void Secrets_AreMasked(string command, string secret)
    {
        var clean = HistorySanitizer.Clean(command);
        Assert.NotNull(clean);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains(HistorySanitizer.Mask, clean);
    }

    [Theory]
    [InlineData("ls -la /var/www")]
    [InlineData("systemctl restart nginx")]
    [InlineData("grep -r password /etc/app.conf")]
    [InlineData("psql -p 5433 -h db")]
    public void OrdinaryCommands_AreKept(string command) =>
        Assert.Equal(command, HistorySanitizer.Clean(command));

    [Fact]
    public void LeadingSpace_IsNotRecorded() => Assert.Null(HistorySanitizer.Clean(" mysql -pSegredo"));

    [Fact]
    public void Search_FindsAcrossSessions_NewestFirst_WithoutDuplicates()
    {
        var file = Path.Combine(_dir, "history.jsonl");
        var history = new CommandHistory(file);
        var web = Guid.NewGuid();
        var db = Guid.NewGuid();
        var t = DateTimeOffset.Now;
        history.Add(web, "web01", "10.0.0.1", "systemctl restart nginx", t.AddMinutes(-3));
        history.Add(db, "db01", "10.0.0.2", "systemctl restart mysql", t.AddMinutes(-2));
        history.Add(web, "web01", "10.0.0.1", "tail -f /var/log/nginx/error.log", t.AddMinutes(-1));
        history.Add(web, "web01", "10.0.0.1", "systemctl restart nginx", t);

        var all = history.Search("restart");
        Assert.Equal(["systemctl restart nginx", "systemctl restart mysql"], all.Select(e => e.Command));

        var onlyDb = history.Search("SYSTEMCTL", db);
        Assert.Single(onlyDb);
        Assert.Equal("db01", onlyDb[0].SessionName);

        // Busca também pelo nome do servidor, sem acento/maiúscula.
        Assert.Equal(2, history.Search("WEB01").Count); // comandos distintos dessa sessão

        // Persistiu: outra instância lê o mesmo arquivo.
        var reopened = new CommandHistory(file);
        Assert.Equal(4, reopened.Count);
    }

    [Fact]
    public void ImmediateRepeats_AreStoredOnce()
    {
        var history = new CommandHistory(Path.Combine(_dir, "h.jsonl"));
        var id = Guid.NewGuid();
        history.Add(id, "s", "h", "uptime");
        history.Add(id, "s", "h", "uptime");
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void CorruptedLines_AreIgnored()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "h.jsonl");
        File.WriteAllText(file, "{\"t\":\"2026-01-01T00:00:00+00:00\",\"s\":\"" + Guid.NewGuid() + "\",\"n\":\"a\",\"h\":\"b\",\"c\":\"ls\"}\n{quebrado\n");
        Assert.Equal(1, new CommandHistory(file).Count);
    }

    [Fact]
    public void Clear_RemovesOnlyTheChosenSession()
    {
        var file = Path.Combine(_dir, "h.jsonl");
        var history = new CommandHistory(file);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        history.Add(a, "a", "h", "ls");
        history.Add(b, "b", "h", "pwd");
        history.Clear(a);
        Assert.Equal(1, history.Count);
        Assert.Equal(1, new CommandHistory(file).Count);
    }
}
