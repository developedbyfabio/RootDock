using RootDock.Core.Security;
using RootDock.Core.Sessions;

namespace RootDock.Core.Tests;

public sealed class SessionLoginsTests
{
    private sealed class MemoryStore : ISecretStore
    {
        public Dictionary<string, (string User, string Secret)> Items { get; } = new();

        public void Save(string key, string userName, string secret) => Items[key] = (userName, secret);

        public string? Read(string key) => Items.TryGetValue(key, out var item) ? item.Secret : null;

        public void Delete(string key) => Items.Remove(key);
    }

    private readonly MemoryStore _store = new();
    private readonly SessionLogins _logins;

    public SessionLoginsTests() => _logins = new SessionLogins(_store);

    [Fact]
    public void Save_AddsLogins_FirstBecomesDefault()
    {
        var entry = new SessionEntry { Host = "srv" };
        _logins.Save(entry, "root", "s1");
        _logins.Save(entry, "deploy", "s2");

        Assert.Equal("root", entry.Username);
        Assert.Equal(["root", "deploy"], entry.Logins);
        Assert.Equal("s2", _logins.ReadPassword(entry, "deploy"));
        Assert.True(_logins.List(entry)[0].IsDefault);
    }

    [Fact]
    public void Save_SameUserTwice_UpdatesPasswordWithoutDuplicating()
    {
        var entry = new SessionEntry();
        _logins.Save(entry, "root", "old");
        _logins.Save(entry, "root", "new");

        Assert.Single(entry.Logins);
        Assert.Equal("new", _logins.ReadPassword(entry, "root"));
    }

    [Fact]
    public void Save_WithoutPassword_KeepsUserOnly()
    {
        var entry = new SessionEntry();
        _logins.Save(entry, "admin", null);

        var login = Assert.Single(_logins.List(entry));
        Assert.False(login.HasPassword);
    }

    [Fact]
    public void Remove_Default_PromotesNext_AndDeletesSecret()
    {
        var entry = new SessionEntry();
        _logins.Save(entry, "root", "a");
        _logins.Save(entry, "deploy", "b");

        _logins.Remove(entry, "root");

        Assert.Equal("deploy", entry.Username);
        Assert.Null(_logins.ReadPassword(entry, "root"));
        Assert.Equal(["deploy"], entry.Logins);
    }

    [Fact]
    public void Remove_Last_WithoutKey_ClearsUser_WithKey_KeepsUser()
    {
        var plain = new SessionEntry();
        _logins.Save(plain, "root", "a");
        _logins.Remove(plain, "root");
        Assert.Null(plain.Username);

        var withKey = new SessionEntry { PrivateKeyPath = @"C:\k\id_ed25519" };
        _logins.Save(withKey, "root", "a");
        _logins.Remove(withKey, "root");
        Assert.Equal("root", withKey.Username);
    }

    [Fact]
    public void Migrate_LegacySinglePassword_BecomesDefaultLogin()
    {
        var entry = new SessionEntry { Username = "root" };
        _store.Save(CredentialVault.SessionPasswordKey(entry.Id), "root", "legada");

        var list = _logins.List(entry);

        var login = Assert.Single(list);
        Assert.Equal("root", login.Username);
        Assert.True(login.HasPassword && login.IsDefault);
        Assert.Equal("legada", _logins.ReadPassword(entry, "root"));
        Assert.Null(_store.Read(CredentialVault.SessionPasswordKey(entry.Id)));
    }

    [Fact]
    public void CopyAll_DuplicatesSecretsForNewSession()
    {
        var source = new SessionEntry();
        _logins.Save(source, "root", "a");
        var copy = source.Clone();
        _logins.CopyAll(source, copy);

        Assert.Equal("a", _logins.ReadPassword(copy, "root"));
        copy.Logins.Add("x");
        Assert.DoesNotContain("x", source.Logins); // listas independentes
    }

    [Fact]
    public void DeleteAll_RemovesEverySecret()
    {
        var entry = new SessionEntry();
        _logins.Save(entry, "root", "a");
        _logins.Save(entry, "deploy", "b");

        _logins.DeleteAll(entry);

        Assert.Empty(_store.Items);
    }
}
