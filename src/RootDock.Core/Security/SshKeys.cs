using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Ssh;

namespace RootDock.Core.Security;

/// <summary>Chave SSH do usuário (par privado/público em ~/.ssh).</summary>
public sealed record SshKeyInfo(string PrivateKeyPath, string PublicKeyPath, string PublicKey, string Type, string Fingerprint, string Comment)
{
    /// <summary>Nome curto do algoritmo para a tela (Ed25519, RSA, ECDSA).</summary>
    public string DisplayType => Type switch
    {
        "ssh-ed25519" => "Ed25519",
        "ssh-rsa" => "RSA",
        _ when Type.StartsWith("ecdsa", StringComparison.Ordinal) => "ECDSA",
        _ => Type,
    };
}

public enum KeyLoginResult
{
    /// <summary>O servidor aceitou a chave.</summary>
    Accepted,

    /// <summary>O servidor recusou a chave (não está no authorized_keys ou o sshd não aceita chaves).</summary>
    Rejected,

    /// <summary>A chave privada tem senha; o teste fica para a próxima conexão.</summary>
    NeedsPassphrase,
}

/// <summary>
/// Cria, lê e instala chaves SSH (o equivalente a ssh-keygen + ssh-copy-id), sem depender de
/// programas externos. A chave nova é Ed25519, no formato OpenSSH, em %USERPROFILE%\.ssh,
/// compatível com o ssh do Windows, Git, VS Code e o próprio RootDock.
/// </summary>
public static partial class SshKeys
{
    /// <summary>Marcas impressas pelo comando de instalação no servidor.</summary>
    public const string AddedMarker = "ROOTDOCK_KEY_ADDED";
    public const string ExistsMarker = "ROOTDOCK_KEY_EXISTS";

    /// <summary>Nomes procurados, na ordem de preferência (os mesmos que o OpenSSH usa).</summary>
    public static readonly string[] DefaultNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Primeira chave padrão que tenha os dois arquivos (privado e .pub).</summary>
    public static SshKeyInfo? FindDefault(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        foreach (var name in DefaultNames)
        {
            var privatePath = Path.Combine(dir, name);
            if (File.Exists(privatePath) && Read(privatePath, privatePath + ".pub") is { } info)
            {
                return info;
            }
        }

        return null;
    }

    public static SshKeyInfo? Read(string privatePath, string publicPath)
    {
        string line;
        try
        {
            line = File.ReadLines(publicPath).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ParsePublicKey(line) is { } parsed
            ? new SshKeyInfo(privatePath, publicPath, line, parsed.Type, parsed.Fingerprint, parsed.Comment)
            : null;
    }

    [GeneratedRegex(@"^(?<type>ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(?:256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) (?<blob>[A-Za-z0-9+/]+={0,2})(?:\s+(?<comment>.*))?$")]
    private static partial Regex PublicKeyPattern();

    [GeneratedRegex(@"[^A-Za-z0-9._@-]")]
    private static partial Regex UnsafeCommentChars();

    /// <summary>Lê uma linha "tipo base64 comentário" e calcula a impressão digital SHA256 (como o ssh-keygen -l).</summary>
    public static (string Type, string Blob, string Fingerprint, string Comment)? ParsePublicKey(string line)
    {
        var match = PublicKeyPattern().Match(line.Trim());
        if (!match.Success)
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(match.Groups["blob"].Value);
        }
        catch (FormatException)
        {
            return null;
        }

        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return (match.Groups["type"].Value, match.Groups["blob"].Value, fingerprint, match.Groups["comment"].Value.Trim());
    }

    /// <summary>Comentário seguro para gravar no servidor (sem espaços, aspas ou símbolos do shell).</summary>
    public static string SafeComment(string comment)
    {
        var safe = UnsafeCommentChars().Replace(comment.Trim().Replace(' ', '-'), "");
        return safe.Length > 64 ? safe[..64] : safe;
    }

    /// <summary>Comentário padrão: usuario@computador, como o ssh-keygen.</summary>
    public static string DefaultComment() => SafeComment($"{Environment.UserName}@{Environment.MachineName}");

    /// <summary>
    /// Cria um par Ed25519 em <paramref name="directory"/>\id_ed25519 (sem senha). Nunca substitui
    /// uma chave existente. O arquivo privado fica acessível só ao usuário (e ao sistema).
    /// </summary>
    public static SshKeyInfo GenerateEd25519(string comment, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var privatePath = Path.Combine(dir, "id_ed25519");
        var publicPath = privatePath + ".pub";
        if (File.Exists(privatePath) || File.Exists(publicPath))
        {
            throw new IOException($"Já existe uma chave em {privatePath}; ela não será substituída.");
        }

        Directory.CreateDirectory(dir);
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();

        var privateBlob = OpenSshPrivateKeyUtilities.EncodePrivateKey(pair.Private);
        var publicBlob = OpenSshPublicKeyUtilities.EncodePublicKey(pair.Public);
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        var base64 = Convert.ToBase64String(privateBlob);
        for (var i = 0; i < base64.Length; i += 70)
        {
            pem.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        }

        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        var publicLine = $"ssh-ed25519 {Convert.ToBase64String(publicBlob)} {SafeComment(comment)}".TrimEnd();

        WritePrivate(privatePath, pem.ToString());
        File.WriteAllText(publicPath, publicLine + "\n", new UTF8Encoding(false));
        return Read(privatePath, publicPath) ?? throw new InvalidOperationException("Chave criada, mas não foi possível lê-la.");
    }

    // O ssh do Windows recusa chaves privadas que outros usuários podem ler: herança desligada,
    // acesso só para o usuário atual, SYSTEM e Administradores.
    private static void WritePrivate(string path, string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Usuário do Windows desconhecido.");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));

        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(bytes);
    }

    /// <summary>
    /// Comando (sh) que adiciona a chave ao ~/.ssh/authorized_keys do usuário, como o ssh-copy-id:
    /// cria a pasta e o arquivo com permissões restritas se não existirem, não duplica a chave,
    /// garante a quebra de linha antes de acrescentar e ajusta o SELinux quando houver.
    /// Não altera nem remove nenhuma linha existente.
    /// </summary>
    public static string InstallCommand(string publicKey)
    {
        var parsed = ParsePublicKey(publicKey) ?? throw new ArgumentException("Chave pública inválida.", nameof(publicKey));
        // Só caracteres seguros: tipo, base64 e comentário saneado. Nada de aspas no script interno.
        var line = $"{parsed.Type} {parsed.Blob} {SafeComment(parsed.Comment)}".TrimEnd();
        var script =
            "umask 077; d=\"$HOME/.ssh\"; f=\"$d/authorized_keys\"; " +
            "mkdir -p \"$d\" && touch \"$f\" || exit 3; " +
            $"if grep -qF \"{parsed.Blob}\" \"$f\"; then echo {ExistsMarker}; exit 0; fi; " +
            "if [ -s \"$f\" ] && [ -n \"$(tail -c1 \"$f\")\" ]; then echo >> \"$f\"; fi; " +
            $"printf \"%s\\n\" \"{line}\" >> \"$f\" || exit 4; " +
            "if command -v restorecon >/dev/null 2>&1; then restorecon -R \"$d\" >/dev/null 2>&1; fi; " +
            $"echo {AddedMarker}";
        // sh explícito: funciona mesmo se o shell de login for fish, zsh ou csh.
        return $"sh -c '{script}'";
    }

    /// <summary>
    /// Tenta entrar só com a chave (sem senha), numa conexão separada, para confirmar que a
    /// instalação funcionou. Servidor desconhecido não é aceito aqui: ele já deve ter sido confirmado.
    /// </summary>
    /// <param name="connectHost">Endereço real quando há bastion (túnel local); null = o próprio servidor.</param>
    public static async Task<KeyLoginResult> TestLoginAsync(
        string host, int port, string username, string privateKeyPath, KnownHostsStore knownHosts, CancellationToken ct,
        string? connectHost = null, int? connectPort = null)
    {
        PrivateKeyFile key;
        try
        {
            key = new PrivateKeyFile(privateKeyPath);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return KeyLoginResult.NeedsPassphrase;
        }

        using (key)
        {
            var info = new ConnectionInfo(connectHost ?? host, connectPort ?? port, username, new PrivateKeyAuthenticationMethod(username, key))
            {
                Timeout = SshClientFactory.InteractionTimeout,
            };
            using var client = new SshClient(info);
            SshClientFactory.AttachHostKeyVerification(client, host, port, knownHosts, TrustedOnly.Instance, () => { });
            try
            {
                await SshClientFactory.ConnectAsync(client, ct).ConfigureAwait(false);
                client.Disconnect();
                return KeyLoginResult.Accepted;
            }
            catch (SshAuthenticationException)
            {
                return KeyLoginResult.Rejected;
            }
        }
    }

    private sealed class TrustedOnly : ISshInteraction
    {
        public static readonly TrustedOnly Instance = new();

        public bool ConfirmHostKey(string host, int port, HostKeyCheck check, byte[] keyBlob) => false;

        public string? AnswerPrompt(string host, string instruction, string prompt, bool echo) => null;
    }
}
