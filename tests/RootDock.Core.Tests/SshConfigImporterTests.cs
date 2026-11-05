using RootDock.Core.Import;

namespace RootDock.Core.Tests;

public sealed class SshConfigImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-sshcfg-" + Guid.NewGuid().ToString("N"));

    public SshConfigImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Hosts_Defaults_Keys_AndJumps()
    {
        var key = Path.Combine(_dir, "id_web");
        File.WriteAllText(key, "chave");
        var config = Path.Combine(_dir, "config");
        File.WriteAllText(config, $"""
            # comentário
            Host bastion
                HostName 203.0.113.10
                User ops
                Port 2200

            Host web01 web-alias
                HostName 10.0.0.11
                IdentityFile {key}
                ProxyJump ops@bastion:2200

            Host db01
                HostName=10.0.0.20   # banco
                ProxyJump desconhecido

            Host *.interno
                User nao-usar

            Host *
                User padrao
                Port 22
            """);

        var result = SshConfigImporter.Import(config);
        var byName = result.Sessions.ToDictionary(s => s.Name);

        Assert.Equal(["bastion", "web01", "web-alias", "db01"], result.Sessions.Select(s => s.Name));
        Assert.Equal(("203.0.113.10", 2200, "ops"), (byName["bastion"].Host, byName["bastion"].Port, byName["bastion"].Username));
        Assert.Equal("10.0.0.11", byName["web01"].Host);
        Assert.Equal("padrao", byName["web01"].Username); // do "Host *"
        Assert.Equal(key, byName["web01"].PrivateKeyPath);
        Assert.Equal(byName["bastion"].Id, byName["web01"].JumpSessionId);
        Assert.Equal("10.0.0.20", byName["db01"].Host);
        Assert.Null(byName["db01"].JumpSessionId);
        Assert.Contains(result.Warnings, w => w.Contains("desconhecido"));
        Assert.All(result.Sessions, s => Assert.Equal(SshConfigImporter.Folder, s.FolderPath));
    }

    [Fact]
    public void Include_AndMatchBlocks()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "conf.d"));
        File.WriteAllText(Path.Combine(_dir, "conf.d", "clientes.conf"), "Host cliente1\n  HostName 192.0.2.5\n  User suporte\n");
        var config = Path.Combine(_dir, "config");
        File.WriteAllText(config, "Include conf.d/*.conf\nMatch host foo\n  User ignorado\nHost local\n  HostName 127.0.0.1\n");

        var result = SshConfigImporter.Import(config);

        Assert.Equal(["cliente1", "local"], result.Sessions.Select(s => s.Name));
        Assert.Equal("suporte", result.Sessions[0].Username);
        Assert.Contains(result.Warnings, w => w.Contains("Match"));
    }

    [Fact]
    public void MissingKey_IsWarned()
    {
        var config = Path.Combine(_dir, "config");
        File.WriteAllText(config, "Host x\n  HostName 1.2.3.4\n  IdentityFile ~/.ssh/nao_existe_rootdock_teste\n");
        var result = SshConfigImporter.Import(config);
        Assert.Null(result.Sessions[0].PrivateKeyPath);
        Assert.Contains(result.Warnings, w => w.Contains("não encontrada"));
    }
}
