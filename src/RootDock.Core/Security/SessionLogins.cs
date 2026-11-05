using RootDock.Core.Sessions;

namespace RootDock.Core.Security;

/// <summary>Onde as senhas ficam (o Cofre do Windows no app; memória nos testes).</summary>
public interface ISecretStore
{
    void Save(string key, string userName, string secret);

    string? Read(string key);

    void Delete(string key);
}

/// <summary>Login salvo numa sessão: o usuário fica no arquivo de sessões; a senha, só no cofre.</summary>
public sealed record SavedLogin(string Username, bool HasPassword, bool IsDefault);

/// <summary>
/// Vários logins (usuário + senha) por sessão. A lista de usuários fica em
/// <see cref="SessionEntry.Logins"/>; cada senha fica no cofre com a chave da sessão e do usuário.
/// O usuário padrão é <see cref="SessionEntry.Username"/>. Senhas da versão anterior (uma por
/// sessão) são migradas na primeira leitura.
/// </summary>
public sealed class SessionLogins
{
    private readonly ISecretStore _store;

    public SessionLogins(ISecretStore store)
    {
        _store = store;
    }

    public static string PasswordKey(Guid sessionId, string username) => $"session:{sessionId:N}:login:{username}";

    /// <summary>Logins da sessão, o padrão primeiro.</summary>
    public IReadOnlyList<SavedLogin> List(SessionEntry entry)
    {
        Migrate(entry);
        var users = entry.Logins.Distinct(StringComparer.Ordinal).ToList();
        return users
            .Select(u => new SavedLogin(u, ReadPassword(entry, u) is not null, string.Equals(u, entry.Username, StringComparison.Ordinal)))
            .OrderByDescending(l => l.IsDefault)
            .ToList();
    }

    public string? ReadPassword(SessionEntry entry, string username) => _store.Read(PasswordKey(entry.Id, username));

    /// <summary>Salva (ou atualiza) um login. Sem senha, só o usuário entra na lista.</summary>
    public void Save(SessionEntry entry, string username, string? password)
    {
        username = username.Trim();
        if (username.Length == 0)
        {
            throw new ArgumentException("Informe o usuário.", nameof(username));
        }

        if (password is not null)
        {
            _store.Save(PasswordKey(entry.Id, username), username, password);
        }

        if (!entry.Logins.Contains(username, StringComparer.Ordinal))
        {
            entry.Logins.Add(username);
        }

        if (string.IsNullOrWhiteSpace(entry.Username))
        {
            entry.Username = username;
        }
    }

    /// <summary>Esquece só a senha (o usuário continua na lista e a senha será pedida).</summary>
    public void ForgetPassword(SessionEntry entry, string username) => _store.Delete(PasswordKey(entry.Id, username));

    /// <summary>Remove o login. Se era o padrão, o próximo vira padrão.</summary>
    public void Remove(SessionEntry entry, string username)
    {
        _store.Delete(PasswordKey(entry.Id, username));
        entry.Logins.RemoveAll(u => string.Equals(u, username, StringComparison.Ordinal));
        if (string.Equals(entry.Username, username, StringComparison.Ordinal))
        {
            // Com chave SSH o usuário continua necessário; sem chave, vazio = perguntar no terminal.
            entry.Username = entry.Logins.FirstOrDefault() ?? (entry.PrivateKeyPath is null ? null : username);
        }
    }

    public void SetDefault(SessionEntry entry, string username)
    {
        if (entry.Logins.Contains(username, StringComparer.Ordinal))
        {
            entry.Username = username;
        }
    }

    /// <summary>Apaga todas as senhas da sessão (ao excluir a sessão).</summary>
    public void DeleteAll(SessionEntry entry)
    {
        foreach (var user in entry.Logins)
        {
            _store.Delete(PasswordKey(entry.Id, user));
        }

        _store.Delete(LegacyPasswordKey(entry.Id));
    }

    /// <summary>Copia as senhas para outra sessão (ao duplicar).</summary>
    public void CopyAll(SessionEntry from, SessionEntry to)
    {
        Migrate(from);
        foreach (var user in from.Logins)
        {
            if (ReadPassword(from, user) is { } password)
            {
                _store.Save(PasswordKey(to.Id, user), user, password);
            }
        }

        to.Logins = [.. from.Logins];
    }

    /// <summary>
    /// Versão anterior: uma senha por sessão, do usuário padrão. Passa para o formato novo.
    /// Retorna true quando algo mudou (a sessão precisa ser salva).
    /// </summary>
    public bool Migrate(SessionEntry entry)
    {
        var legacyKey = LegacyPasswordKey(entry.Id);
        if (string.IsNullOrWhiteSpace(entry.Username) || _store.Read(legacyKey) is not { } legacy)
        {
            return false;
        }

        var user = entry.Username.Trim();
        if (_store.Read(PasswordKey(entry.Id, user)) is null)
        {
            _store.Save(PasswordKey(entry.Id, user), user, legacy);
        }

        if (!entry.Logins.Contains(user, StringComparer.Ordinal))
        {
            entry.Logins.Add(user);
        }

        _store.Delete(legacyKey);
        return true;
    }

    private static string LegacyPasswordKey(Guid sessionId) => CredentialVault.SessionPasswordKey(sessionId);
}
