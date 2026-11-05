using System.Security.Cryptography;
using System.Text;
using RootDock.Core.Security;
using RootDock.Core.Sessions;

namespace RootDock.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-tests-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SessionStore_round_trips_and_keeps_backup()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"));
        var document = new SessionDocument();
        document.Sessions.Add(new SessionEntry { Name = "web", Host = "10.0.0.5", FolderPath = @"A\B", Environment = SessionEnvironment.Production });
        store.Save(document);
        document.Sessions.Add(new SessionEntry { Name = "db", Host = "10.0.0.6" });
        store.Save(document);

        var loaded = store.Load();

        Assert.Equal(2, loaded.Sessions.Count);
        Assert.Equal("A/B", loaded.Sessions[0].FolderPath);
        Assert.Equal(SessionEnvironment.Production, loaded.Sessions[0].Environment);
        Assert.True(File.Exists(store.FilePath + ".bak"));
    }

    [Fact]
    public void SessionStore_never_overwrites_a_corrupted_file()
    {
        var path = Path.Combine(_dir, "sessions.json");
        File.WriteAllText(path, "{ isto não é json");
        var store = new SessionStore(path);

        Assert.Throws<SessionStoreCorruptedException>(() => store.Load());
        Assert.Equal("{ isto não é json", File.ReadAllText(path));
    }

    [Fact]
    public void KnownHosts_trust_then_detect_changed_key()
    {
        var store = new KnownHostsStore(Path.Combine(_dir, "known_hosts"));
        var key = FakeKeyBlob("ssh-ed25519", 1);
        var other = FakeKeyBlob("ssh-ed25519", 2);

        Assert.Equal(HostKeyStatus.Unknown, store.Check("Server.Local", 2222, key, "ssh-ed25519").Status);

        store.Trust("Server.Local", 2222, key, "ssh-ed25519");

        Assert.Equal(HostKeyStatus.Trusted, store.Check("server.local", 2222, key, "ssh-ed25519").Status);
        Assert.Equal(HostKeyStatus.Changed, store.Check("server.local", 2222, other, "ssh-ed25519").Status);
        Assert.Equal(HostKeyStatus.Unknown, store.Check("server.local", 22, key, "ssh-ed25519").Status);
        Assert.StartsWith("[server.local]:2222 ssh-ed25519 ", File.ReadAllText(Path.Combine(_dir, "known_hosts")));
    }

    [Fact]
    public void KnownHosts_reads_hashed_openssh_entries()
    {
        var key = FakeKeyBlob("ssh-rsa", 3);
        var salt = RandomNumberGenerator.GetBytes(20);
        var hash = HMACSHA1.HashData(salt, Encoding.ASCII.GetBytes("10.1.2.3"));
        var openSsh = Path.Combine(_dir, "openssh_known_hosts");
        File.WriteAllText(openSsh, $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-rsa {Convert.ToBase64String(key)}\n");
        var store = new KnownHostsStore(Path.Combine(_dir, "known_hosts"), [openSsh]);

        var check = store.Check("10.1.2.3", 22, key, "rsa-sha2-512");

        Assert.Equal(HostKeyStatus.Trusted, check.Status);
        Assert.Equal("ssh-rsa", check.KeyType);
    }

    private static byte[] FakeKeyBlob(string type, byte seed)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + 32];
        blob[3] = (byte)name.Length;
        name.CopyTo(blob, 4);
        blob.AsSpan(4 + name.Length).Fill(seed);
        return blob;
    }
}
