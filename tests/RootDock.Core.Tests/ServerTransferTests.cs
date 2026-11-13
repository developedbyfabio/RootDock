using System.Diagnostics;
using RootDock.Core.Security;
using RootDock.Core.ServerTransfer;
using RootDock.Core.Sftp;
using RootDock.Core.Ssh;

namespace RootDock.Core.Tests;

/// <summary>Transferência entre servidores: leitura do rsync, comandos do modo direto e a cópia via SFTP.</summary>
public class ServerTransferTests
{
    // ------------------------------------------------------------------ saída do rsync
    [Theory]
    [InlineData("    1,238,099,968  45%   12.34MB/s    0:01:23 (xfr#5, to-chk=169/396)", 1_238_099_968L, 45)]
    [InlineData("              0   0%    0.00kB/s    0:00:00 (xfr#0, to-chk=0/3)", 0L, 0)]
    [InlineData("  5.368.709.120 100%  110,52MB/s    0:00:46 (xfr#1, to-chk=0/1)", 5_368_709_120L, 100)]
    [InlineData("         32,768   3%   31.25kB/s    0:00:30", 32_768L, 3)]
    public void Progress_line_gives_bytes_and_percent(string line, long bytes, int percent)
    {
        var progress = RsyncOutput.ParseProgress(line);

        Assert.NotNull(progress);
        Assert.Equal(bytes, progress.Value.Bytes);
        Assert.Equal(percent, progress.Value.Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sending incremental file list")]
    [InlineData("rsync: [sender] link_stat \"/x\" failed: No such file or directory (2)")]
    [InlineData("pasta/arquivo 100%.txt")]
    public void Other_lines_are_not_progress(string line) => Assert.Null(RsyncOutput.ParseProgress(line));

    [Theory]
    [InlineData("rsync  version 3.2.7  protocol version 31", 3, 2, 7)]
    [InlineData("rsync  version 3.1.2  protocol version 31", 3, 1, 2)]
    [InlineData("rsync  version 3.4.1  protocol version 32", 3, 4, 1)]
    [InlineData("rsync  version 3.0.9  protocol version 30", 3, 0, 9)]
    public void Version_line_is_read(string line, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), RsyncOutput.ParseVersion(line));

    [Fact]
    public void Old_rsync_is_below_the_minimum()
    {
        Assert.True(RsyncOutput.ParseVersion("rsync  version 3.0.9  protocol version 30") < RsyncOutput.MinimumVersion);
        Assert.True(RsyncOutput.ParseVersion("rsync  version 3.1.0  protocol version 31") >= RsyncOutput.MinimumVersion);
        Assert.Null(RsyncOutput.ParseVersion("bash: rsync: command not found"));
    }

    // ------------------------------------------------------------------ o que entra nos comandos
    [Theory]
    [InlineData("/tmp/rootdock-xfer.Ab12Cd34", true)]
    [InlineData("/var/tmp/x/rootdock-xfer.q", true)]
    [InlineData("/tmp/rootdock-xfer.Ab12/../../etc", false)]
    [InlineData("/tmp/rootdock-xfer.Ab12 /etc", false)]
    [InlineData("/", false)]
    [InlineData("/tmp", false)]
    [InlineData("rootdock-xfer.abc", false)]
    [InlineData("/tmp/rootdock-xfer.", false)]
    public void Only_our_work_directory_is_accepted(string path, bool expected)
    {
        Assert.Equal(expected, DirectScripts.IsWorkDirectory(path));
        if (!expected)
        {
            Assert.Throws<ArgumentException>(() => DirectScripts.RemoveWorkDirectory(path));
        }
    }

    [Fact]
    public void Public_key_is_reduced_to_type_and_data()
    {
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIabc+/0=",
            DirectScripts.CleanPublicKey("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIabc+/0= rootdock-transfer-x\r"));
        Assert.Null(DirectScripts.CleanPublicKey("ssh-rsa AAAAB3Nza comentario"));
        Assert.Null(DirectScripts.CleanPublicKey("ssh-ed25519 AAAA';rm -rf /;' x"));
        Assert.Null(DirectScripts.CleanPublicKey(""));
    }

    [Theory]
    [InlineData("root", true)]
    [InlineData("deploy.user-1", true)]
    [InlineData("-oProxyCommand=x", false)]
    [InlineData("a b", false)]
    [InlineData("user;id", false)]
    [InlineData("", false)]
    public void User_names_that_could_become_options_are_refused(string user, bool expected) =>
        Assert.Equal(expected, DirectScripts.IsUserName(user));

    [Fact]
    public void Rsync_command_carries_the_options_that_keep_the_copy_safe()
    {
        var script = DirectScripts.Rsync("/tmp/rootdock-xfer.Ab1", "/tmp/rootdock-xfer.Ab1/pid.1", "/tmp/rootdock-xfer.Ab1/list.1",
            "/srv/www/meu site", "/dados/o'destino", "root", "10.0.0.2", 2222, preserve: true, skipExisting: false);

        Assert.Contains("-rlptgoD", script);
        Assert.Contains("--partial-dir=.rootdock-partial", script); // nada pela metade no nome final
        Assert.Contains("--info=progress2", script);
        Assert.Contains("--from0", script);
        Assert.Contains("StrictHostKeyChecking=yes", script);
        Assert.Contains("IdentitiesOnly=yes", script);
        Assert.Contains("-p 2222", script);
        Assert.Contains("cd '/srv/www/meu site'", script);
        Assert.Contains(@"'root@10.0.0.2:/dados/o'\''destino/'", script);
        Assert.DoesNotContain("--ignore-existing", script);

        var plain = DirectScripts.Rsync("/tmp/rootdock-xfer.Ab1", "p", "l", "/a", "/b", "root", "fe80::1", 22, preserve: false, skipExisting: true);
        Assert.Contains(" -rl --ignore-existing ", plain);
        Assert.DoesNotContain("-rlptgoD", plain);
        Assert.Contains("root@[fe80::1]:/b/", plain);
    }

    // ------------------------------------------------------------------ comandos rodando de verdade (shell local, pasta isolada)
    private static string? FindBash() =>
        new[] { @"C:\Program Files\Git\usr\bin\bash.exe", @"C:\Program Files\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);

    private static string Unix(string path) => "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

    private static (int Exit, string Output, string Error) Sh(string bash, string home, string script)
    {
        var start = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // O texto vai num arquivo: assim as aspas do comando não passam pela linha de comando do Windows.
        var file = Path.Combine(home, "run-" + Guid.NewGuid().ToString("N")[..6] + ".sh");
        File.WriteAllText(file, script + "\n");
        start.ArgumentList.Add(Unix(file));
        start.Environment["HOME"] = Unix(home);
        start.Environment["TMPDIR"] = Unix(home);
        start.Environment["PATH"] = Path.GetDirectoryName(bash) + ";" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        File.Delete(file);
        return (process.ExitCode, output, error);
    }

    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "rootdock-xfer-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        return home;
    }

    [Fact]
    public void Authorize_adds_one_line_drops_stale_ones_and_revoke_removes_only_ours()
    {
        Assert.SkipUnless(FindBash() is not null, "Git Bash não instalado.");
        var bash = FindBash()!;
        var home = NewHome();
        try
        {
            var file = Path.Combine(home, ".ssh", "authorized_keys");
            const string mine = "ssh-rsa AAAAB3NzaC1yc2E= fabio@notebook";
            const string stale = "no-pty ssh-ed25519 AAAAvelha rootdock-transfer-202501010000-aaaaaaaaaaaa";
            const string recent = "no-pty ssh-ed25519 AAAAnova rootdock-transfer-202611130900-bbbbbbbbbbbb";
            File.WriteAllText(file, $"{stale}\n{recent}\n{mine}"); // a última linha sem quebra, como muita gente deixa
            const string comment = "rootdock-transfer-202611131000-cccccccccccc";

            var add = Sh(bash, home, DirectScripts.Authorize("ssh-ed25519 AAAAC3Nza+/x=", comment, staleBefore: "202611120000"));
            Assert.True(add.Exit == 0, add.Error);
            var lines = File.ReadAllLines(file);
            Assert.Equal(3, lines.Length);
            Assert.Equal(recent, lines[0]);
            Assert.Equal(mine, lines[1]);
            Assert.Equal($"no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty ssh-ed25519 AAAAC3Nza+/x= {comment}", lines[2]);

            var remove = Sh(bash, home, DirectScripts.Revoke(comment));
            Assert.True(remove.Exit == 0, remove.Error);
            Assert.Equal([recent, mine], File.ReadAllLines(file));
            Assert.False(File.Exists(file + ".rootdock-tmp"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Authorize_creates_the_file_and_revoke_can_leave_it_empty()
    {
        Assert.SkipUnless(FindBash() is not null, "Git Bash não instalado.");
        var bash = FindBash()!;
        var home = NewHome();
        try
        {
            Directory.Delete(Path.Combine(home, ".ssh"));
            const string comment = "rootdock-transfer-202611131000-cccccccccccc";

            Assert.Equal(0, Sh(bash, home, DirectScripts.Authorize("ssh-ed25519 AAAAC3Nza=", comment, "202611120000")).Exit);
            var file = Path.Combine(home, ".ssh", "authorized_keys");
            Assert.Single(File.ReadAllLines(file));

            Assert.Equal(0, Sh(bash, home, DirectScripts.Revoke(comment)).Exit);
            Assert.Empty(File.ReadAllLines(file));
            Assert.Equal(0, Sh(bash, home, DirectScripts.Revoke(comment)).Exit); // de novo: nada a tirar, sem erro
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Key_is_created_in_a_private_folder_and_removed_afterwards()
    {
        Assert.SkipUnless(FindBash() is not null, "Git Bash não instalado.");
        var bash = FindBash()!;
        var home = NewHome();
        try
        {
            var created = Sh(bash, home, DirectScripts.CreateKey("rootdock-transfer-202611131000-cccccccccccc"));
            Assert.True(created.Exit == 0, created.Error);
            var lines = created.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);
            Assert.True(DirectScripts.IsWorkDirectory(lines[0]), lines[0]);
            Assert.NotNull(DirectScripts.CleanPublicKey(lines[1]));
            var folder = Directory.GetDirectories(home, "rootdock-xfer.*").Single();
            Assert.True(File.Exists(Path.Combine(folder, "key")));

            var known = Sh(bash, home, DirectScripts.WriteKnownHosts(lines[0], ["[10.0.0.2]:2222 ssh-ed25519 AAAAhost", "10.0.0.2 ssh-rsa AAAAoutra"]));
            Assert.True(known.Exit == 0, known.Error);
            Assert.Equal(["[10.0.0.2]:2222 ssh-ed25519 AAAAhost", "10.0.0.2 ssh-rsa AAAAoutra"], File.ReadAllLines(Path.Combine(folder, "known_hosts")));

            Assert.Equal(0, Sh(bash, home, DirectScripts.RemoveWorkDirectory(lines[0])).Exit);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Every_script_is_valid_shell_even_with_awkward_names()
    {
        Assert.SkipUnless(FindBash() is not null, "Git Bash não instalado.");
        var bash = FindBash()!;
        var home = NewHome();
        try
        {
            const string dir = "/tmp/rootdock-xfer.Ab12Cd34";
            string[] scripts =
            [
                DirectScripts.ProbeSource(),
                DirectScripts.ProbeTarget(),
                DirectScripts.CreateKey("rootdock-transfer-202611131000-cccccccccccc"),
                DirectScripts.WriteKnownHosts(dir, ["h ssh-ed25519 AAAA"]),
                DirectScripts.Authorize("ssh-ed25519 AAAA=", "rootdock-transfer-202611131000-cccccccccccc", "202611120000"),
                DirectScripts.Revoke("rootdock-transfer-202611131000-cccccccccccc"),
                DirectScripts.Test(dir, "root", "10.0.0.2", 22),
                DirectScripts.TotalSize("/srv/it's here", ["a b", "c'd", "$(reboot)", "-rf"]),
                DirectScripts.Rsync(dir, dir + "/pid.1", dir + "/list.1", "/srv/it's \"here\"", "/dst/$HOME `x`", "root", "10.0.0.2", 22, true, true),
                DirectScripts.Kill(dir + "/pid.1"),
                DirectScripts.RemoveWorkDirectory(dir),
            ];
            foreach (var script in scripts)
            {
                var file = Path.Combine(home, "s.sh");
                File.WriteAllText(file, DirectScripts.Wrap(script) + "\n");
                var check = Sh(bash, home, $"bash -n '{Unix(file)}' && sh -n '{Unix(file)}'");
                Assert.True(check.Exit == 0, script + "\n" + check.Error);
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------ cópia via SFTP num servidor de verdade
    /// <summary>
    /// Só roda com um servidor SSH de teste: ROOTDOCK_SSH_TEST=1, ROOTDOCK_SSH_HOST, ROOTDOCK_SSH_PORT,
    /// ROOTDOCK_SSH_USER, ROOTDOCK_SSH_KEY, ROOTDOCK_SSH_KNOWN_HOSTS e ROOTDOCK_SSH_DIR (pasta de
    /// rascunho no servidor, no formato do SFTP). As duas pontas são conexões separadas.
    /// </summary>
    [Fact]
    public async Task Relay_copies_folders_between_two_connections_and_resumes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ROOTDOCK_SSH_TEST") == "1" &&
                          Environment.GetEnvironmentVariable("ROOTDOCK_SSH_DIR") is not null, "Servidor SSH de teste não configurado.");
        var ct = TestContext.Current.CancellationToken;
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
        var knownHosts = new KnownHostsStore(Env("ROOTDOCK_SSH_KNOWN_HOSTS"));
        var credentials = new SshCredentials(Env("ROOTDOCK_SSH_USER"), null, Env("ROOTDOCK_SSH_KEY"), null);
        var host = Env("ROOTDOCK_SSH_HOST");
        var port = int.Parse(Env("ROOTDOCK_SSH_PORT"));
        using var source = await RemoteFileSystem.ConnectAsync(host, port, credentials, knownHosts, new Refuse(), ct);
        var target = await RemoteFileSystem.ConnectAsync(host, port, credentials, knownHosts, new Refuse(), ct);

        var root = RemotePath.Combine(Env("ROOTDOCK_SSH_DIR"), "relay-" + Guid.NewGuid().ToString("N")[..8]);
        var from = RemotePath.Combine(root, "origem");
        var to = RemotePath.Combine(root, "destino");
        await source.CreateDirectoryAsync(root, ct);
        await source.CreateDirectoryAsync(from, ct);
        await source.CreateDirectoryAsync(RemotePath.Combine(from, "pasta"), ct);
        await source.CreateDirectoryAsync(RemotePath.Combine(from, "pasta/sub dir"), ct);
        await source.CreateDirectoryAsync(RemotePath.Combine(from, "pasta/vazia"), ct);
        var big = new byte[24 << 20];
        new Random(7).NextBytes(big);
        var small = "conteúdo pequeno\n"u8.ToArray();
        await source.WriteAllBytesAsync(RemotePath.Combine(from, "pasta/grande.bin"), big, ct);
        await source.WriteAllBytesAsync(RemotePath.Combine(from, "pasta/sub dir/nota 'a'.txt"), small, ct);
        await source.WriteAllBytesAsync(RemotePath.Combine(from, "solto.txt"), small, ct);
        await source.WriteAllBytesAsync(RemotePath.Combine(from, "vazio.dat"), [], ct);
        var old = new DateTime(2024, 3, 5, 10, 20, 30, DateTimeKind.Local);
        await source.SetModifiedAsync(RemotePath.Combine(from, "pasta/grande.bin"), old, ct);
        await source.SetModifiedAsync(RemotePath.Combine(from, "solto.txt"), old, ct);
        try
        {
            var request = new CopyRequest(from, to,
                [new CopyItem("pasta", true, 0), new CopyItem("solto.txt", false, small.Length), new CopyItem("vazio.dat", false, 0)]);
            var relay = new ServerRelay(source, target, request);

            // Cópia direta, sem pausa, para outra pasta.
            var plain = await new ServerRelay(source, target, request with { TargetRoot = RemotePath.Combine(root, "sem pausa") })
                .RunAsync(false, _ => { }, _ => { }, () => false, ct);
            Assert.Equal(4, plain.Copied);
            Assert.Equal(big, await target.ReadAllBytesAsync(RemotePath.Combine(root, "sem pausa/pasta/grande.bin"), 64 << 20, ct));

            // 1ª tentativa: pausa no meio do arquivo grande.
            using var pause = new CancellationTokenSource();
            long total = 0;
            long done = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RunAsync(false, t => total = t, d =>
            {
                done = d;
                if (d > 4 << 20)
                {
                    pause.Cancel();
                }
            }, () => true, pause.Token));
            Assert.Equal(big.Length + small.Length * 2, total);
            Assert.False(await target.ExistsAsync(RemotePath.Combine(to, "pasta/grande.bin"), ct)); // nada pela metade no nome final
            var partial = await target.StatAsync(RemotePath.Combine(to, "pasta/grande.bin") + LocalFiles.PartialSuffix, ct);
            Assert.NotNull(partial);
            Assert.InRange(partial.Value.Size, 1, big.Length - 1);

            // 2ª: continua do parcial e termina. O servidor de teste é Windows, que não deixa trocar o
            // nome de um arquivo que a tentativa cancelada ainda segura aberto (no Linux deixa): a
            // retomada usa uma conexão nova com o destino.
            target.Dispose();
            target = await RemoteFileSystem.ConnectAsync(host, port, credentials, knownHosts, new Refuse(), ct);
            relay = new ServerRelay(source, target, request);
            long resumedFrom = -1;
            var summary = await relay.RunAsync(true, _ => { }, d =>
            {
                if (resumedFrom < 0)
                {
                    resumedFrom = d;
                }

                done = d;
            }, () => false, ct);
            Assert.True(resumedFrom > 0, "a retomada recomeçou do zero");
            Assert.Equal(total, done);
            Assert.Equal(0, summary.Skipped);

            Assert.Equal(big, await target.ReadAllBytesAsync(RemotePath.Combine(to, "pasta/grande.bin"), 64 << 20, ct));
            Assert.Equal(small, await target.ReadAllBytesAsync(RemotePath.Combine(to, "pasta/sub dir/nota 'a'.txt"), 1 << 20, ct));
            Assert.Equal(small, await target.ReadAllBytesAsync(RemotePath.Combine(to, "solto.txt"), 1 << 20, ct));
            Assert.Equal(0, (await target.StatAsync(RemotePath.Combine(to, "vazio.dat"), ct))!.Value.Size);
            Assert.True(await target.IsDirectoryAsync(RemotePath.Combine(to, "pasta/vazia"), ct));
            Assert.False(await target.ExistsAsync(RemotePath.Combine(to, "pasta/grande.bin") + LocalFiles.PartialSuffix, ct));
            var copied = await target.StatAsync(RemotePath.Combine(to, "pasta/grande.bin"), ct);
            Assert.True(Math.Abs((copied!.Value.Modified - old).TotalSeconds) <= 2, $"data {copied.Value.Modified:o}");

            // "Não substituir": o destino alterado fica como está.
            await target.WriteAllBytesAsync(RemotePath.Combine(to, "solto.txt"), "outro"u8.ToArray(), ct);
            var keep = await new ServerRelay(source, target, request with { SkipExisting = true }).RunAsync(false, _ => { }, _ => { }, () => false, ct);
            Assert.Equal(0, keep.Copied);
            Assert.Equal("outro"u8.ToArray(), await target.ReadAllBytesAsync(RemotePath.Combine(to, "solto.txt"), 1 << 20, ct));

            // Padrão: atualiza pelo da origem. Sem "manter data", a data é a de agora.
            var update = await new ServerRelay(source, target, request with { Preserve = false }).RunAsync(false, _ => { }, _ => { }, () => false, ct);
            Assert.Equal(4, update.Copied);
            Assert.Equal(small, await target.ReadAllBytesAsync(RemotePath.Combine(to, "solto.txt"), 1 << 20, ct));
            var fresh = await target.StatAsync(RemotePath.Combine(to, "solto.txt"), ct);
            Assert.True((DateTime.Now - fresh!.Value.Modified).Duration() < TimeSpan.FromMinutes(5), $"data {fresh.Value.Modified:o}");
        }
        finally
        {
            try
            {
                await source.DeleteAsync(await source.GetAsync(root, CancellationToken.None), CancellationToken.None);
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException)
            {
                // A limpeza não pode esconder a falha do teste (no Windows, arquivo ainda aberto não apaga).
            }

            target.Dispose();
        }
    }

    private sealed class Refuse : ISshInteraction
    {
        public bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob) => false;

        public string? AnswerPrompt(string host, string instruction, string prompt, bool echo) => null;
    }
}
