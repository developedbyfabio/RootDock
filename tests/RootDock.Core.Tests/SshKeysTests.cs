using System.Diagnostics;
using System.Security.AccessControl;
using Renci.SshNet;
using RootDock.Core.Security;

namespace RootDock.Core.Tests;

public sealed class SshKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GenerateEd25519_CreatesOpenSshKeyReadableBySshNet()
    {
        var key = SshKeys.GenerateEd25519("teste@pc", _dir);

        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal("teste@pc", key.Comment);
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", File.ReadAllText(key.PrivateKeyPath));

        using var loaded = new PrivateKeyFile(key.PrivateKeyPath);
        Assert.Contains(loaded.HostKeyAlgorithms, a => a.Name == "ssh-ed25519");
    }

    [Fact]
    public void GenerateEd25519_PrivateFileIsNotInheritedByOtherUsers()
    {
        var key = SshKeys.GenerateEd25519("teste", _dir);
        var acl = new FileInfo(key.PrivateKeyPath).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545"); // Todos, Usuários autenticados, Usuários
    }

    [Fact]
    public void GenerateEd25519_NeverOverwritesExistingKey()
    {
        SshKeys.GenerateEd25519("a", _dir);
        Assert.Throws<IOException>(() => SshKeys.GenerateEd25519("b", _dir));
    }

    [Fact]
    public void FindDefault_PrefersEd25519AndNeedsPublicFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "id_rsa"), "x");
        Assert.Null(SshKeys.FindDefault(_dir)); // sem .pub não conta

        var created = SshKeys.GenerateEd25519("eu", _dir);
        Assert.Equal(created.PrivateKeyPath, SshKeys.FindDefault(_dir)?.PrivateKeyPath);
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK2Wb0lHpbC0Z/tt1+o2aWyQvA6XU+k1wqE2hX0x9mRb fabio@pc", "fabio@pc")]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC7 ", "")]
    public void ParsePublicKey_ReadsTypeAndComment(string line, string comment)
    {
        var parsed = SshKeys.ParsePublicKey(line);
        Assert.NotNull(parsed);
        Assert.Equal(comment, parsed.Value.Comment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACB")]
    [InlineData("ssh-ed25519 AAAA'; rm -rf ~; echo ' x")]
    public void InstallCommand_RejectsInvalidKeys(string line) =>
        Assert.Throws<ArgumentException>(() => SshKeys.InstallCommand(line));

    [Fact]
    public void InstallCommand_StripsDangerousCommentCharacters()
    {
        var command = SshKeys.InstallCommand("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK2Wb0lHpbC0Z/tt1+o2aWyQvA6XU+k1wqE2hX0x9mRb a$(whoami)`id`\"b");
        var inner = command["sh -c '".Length..^1];
        Assert.DoesNotContain('\'', inner);
        Assert.DoesNotContain("$(whoami)", inner);
        Assert.DoesNotContain('`', inner);
        Assert.Contains("awhoamiidb", inner);
    }

    /// <summary>Executa o script de verdade num sh (Git para Windows), com um HOME temporário.</summary>
    [Fact]
    public void InstallCommand_AddsOnce_KeepsExistingLines_AndFixesMissingNewline()
    {
        var sh = new[] { @"C:\Program Files\Git\usr\bin\sh.exe", @"C:\Program Files\Git\bin\sh.exe" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(sh is null, "sh do Git para Windows não instalado");

        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        var authorized = Path.Combine(home, ".ssh", "authorized_keys");
        File.WriteAllText(authorized, "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC7 antiga"); // sem quebra de linha no fim

        var key = SshKeys.GenerateEd25519("eu@pc", _dir);
        var command = SshKeys.InstallCommand(key.PublicKey);

        Assert.Contains(SshKeys.AddedMarker, Run(sh!, home, command));
        Assert.Contains(SshKeys.ExistsMarker, Run(sh!, home, command));

        var lines = File.ReadAllLines(authorized);
        Assert.Equal(2, lines.Length);
        Assert.Equal("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC7 antiga", lines[0]);
        Assert.Equal(key.PublicKey, lines[1]);
    }

    private static string Run(string sh, string home, string command)
    {
        // O comando é "sh -c '<script>'", igual ao que o servidor recebe; aqui o sh local o executa.
        var start = new ProcessStartInfo(sh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment["HOME"] = home.Replace('\\', '/');
        // sh, grep, tail e companhia vêm da mesma pasta do Git para Windows.
        start.Environment["PATH"] = Path.GetDirectoryName(sh) + ";" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return output;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
