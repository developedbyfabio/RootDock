using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RootDock.Core.Security;

public enum HostKeyStatus
{
    /// <summary>Chave já conhecida e igual.</summary>
    Trusted,

    /// <summary>Servidor nunca visto (ou visto com outro tipo de chave).</summary>
    Unknown,

    /// <summary>Servidor conhecido, mas a chave mudou. Pode ser reinstalação ou ataque.</summary>
    Changed,
}

public sealed record HostKeyCheck(HostKeyStatus Status, string KeyType, string FingerprintSha256, string? Source);

/// <summary>
/// Chaves de servidores conhecidos no formato OpenSSH.
/// Grava apenas no arquivo do RootDock; consulta (somente leitura) também o ~/.ssh/known_hosts do usuário,
/// incluindo entradas com nome de host em hash (|1|salt|hash).
/// </summary>
public sealed class KnownHostsStore
{
    private readonly string _ownFile;
    private readonly IReadOnlyList<string> _readOnlyFiles;
    private readonly object _gate = new();
    private readonly HashSet<string> _sessionTrusted = new(StringComparer.Ordinal);

    public KnownHostsStore(string ownFile, IReadOnlyList<string>? readOnlyFiles = null)
    {
        _ownFile = ownFile;
        _readOnlyFiles = readOnlyFiles ?? [];
    }

    public static IReadOnlyList<string> DefaultOpenSshFiles()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        return [file];
    }

    /// <summary>Aceita a chave só enquanto o RootDock estiver aberto ("Só desta vez").</summary>
    public void TrustForSession(string host, int port, byte[] keyBlob)
    {
        lock (_gate)
        {
            _sessionTrusted.Add($"{HostToken(host, port)} {Convert.ToBase64String(keyBlob)}");
        }
    }

    public HostKeyCheck Check(string host, int port, byte[] keyBlob, string fallbackKeyType)
    {
        var keyType = ReadKeyType(keyBlob) ?? fallbackKeyType;
        var fingerprint = FingerprintSha256(keyBlob);
        var token = HostToken(host, port);
        var key64 = Convert.ToBase64String(keyBlob);

        lock (_gate)
        {
            if (_sessionTrusted.Contains($"{token} {key64}"))
            {
                return new HostKeyCheck(HostKeyStatus.Trusted, keyType, fingerprint, null);
            }
        }

        var changed = false;
        string? changedSource = null;
        foreach (var file in new[] { _ownFile }.Concat(_readOnlyFiles))
        {
            foreach (var entry in ReadEntries(file))
            {
                if (!entry.Matches(token) || !string.Equals(entry.KeyType, keyType, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(entry.Key64, key64, StringComparison.Ordinal))
                {
                    return new HostKeyCheck(HostKeyStatus.Trusted, keyType, fingerprint, file);
                }

                changed = true;
                changedSource ??= file;
            }
        }

        return new HostKeyCheck(changed ? HostKeyStatus.Changed : HostKeyStatus.Unknown, keyType, fingerprint, changedSource);
    }

    /// <summary>Grava a chave como confiável, substituindo a anterior do mesmo tipo para este host.</summary>
    public void Trust(string host, int port, byte[] keyBlob, string fallbackKeyType)
    {
        var keyType = ReadKeyType(keyBlob) ?? fallbackKeyType;
        var token = HostToken(host, port);
        lock (_gate)
        {
            var lines = File.Exists(_ownFile) ? File.ReadAllLines(_ownFile).ToList() : [];
            lines.RemoveAll(line =>
            {
                var entry = KnownHostEntry.TryParse(line);
                return entry is not null && entry.Matches(token) && entry.KeyType == keyType;
            });
            lines.Add($"{token} {keyType} {Convert.ToBase64String(keyBlob)}");

            Directory.CreateDirectory(Path.GetDirectoryName(_ownFile)!);
            var temp = _ownFile + ".tmp";
            File.WriteAllLines(temp, lines);
            File.Move(temp, _ownFile, overwrite: true);
        }
    }

    /// <summary>Como o OpenSSH grava o host: minúsculo, e "[host]:porta" quando a porta não é 22.</summary>
    public static string HostToken(string host, int port)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return port == 22 ? normalized : $"[{normalized}]:{port}";
    }

    public static string FingerprintSha256(byte[] keyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');

    /// <summary>O blob da chave pública começa com o nome do tipo (string SSH: uint32 tamanho + bytes).</summary>
    internal static string? ReadKeyType(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length == 0 || length > 64 || 4 + length > blob.Length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(blob, 4, (int)length);
    }

    private IEnumerable<KnownHostEntry> ReadEntries(string file)
    {
        string[] lines;
        try
        {
            if (!File.Exists(file))
            {
                yield break;
            }

            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var entry = KnownHostEntry.TryParse(line);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    private sealed class KnownHostEntry
    {
        private readonly string[] _patterns;

        private KnownHostEntry(string[] patterns, string keyType, string key64)
        {
            _patterns = patterns;
            KeyType = keyType;
            Key64 = key64;
        }

        public string KeyType { get; }

        public string Key64 { get; }

        public static KnownHostEntry? TryParse(string line)
        {
            var trimmed = line.Trim();
            // Ignora comentários e marcadores (@cert-authority, @revoked), que não são usados aqui.
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '@')
            {
                return null;
            }

            var parts = trimmed.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return null;
            }

            return new KnownHostEntry(parts[0].Split(','), parts[1], parts[2]);
        }

        public bool Matches(string token)
        {
            foreach (var pattern in _patterns)
            {
                if (pattern.StartsWith("|1|", StringComparison.Ordinal))
                {
                    if (HashedMatches(pattern, token))
                    {
                        return true;
                    }
                }
                else if (string.Equals(pattern, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HashedMatches(string pattern, string token)
        {
            // |1|<salt base64>|<HMAC-SHA1(salt, host) base64>
            var pieces = pattern.Split('|');
            if (pieces.Length != 4)
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(pieces[2]);
                var expected = Convert.FromBase64String(pieces[3]);
                var actual = HMACSHA1.HashData(salt, Encoding.ASCII.GetBytes(token));
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
