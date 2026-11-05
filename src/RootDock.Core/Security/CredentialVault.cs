using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RootDock.Core.Security;

/// <summary>
/// Guarda senhas no Gerenciador de Credenciais do Windows (protegido pelo login do usuário).
/// Nada é gravado em arquivo pelo RootDock.
/// </summary>
public sealed class CredentialVault : ISecretStore
{
    private const string Prefix = "RootDock:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static CredentialVault Default { get; } = new();

    /// <summary>Senha única da versão anterior; hoje usada só para migrar (ver SessionLogins).</summary>
    public static string SessionPasswordKey(Guid sessionId) => $"session:{sessionId:N}:password";

    public static string KeyPassphraseKey(Guid sessionId) => $"session:{sessionId:N}:keypass";

    /// <summary>Senha do VNC da sessão (o VNC não tem usuário, só senha).</summary>
    public static string VncPasswordKey(Guid sessionId) => $"session:{sessionId:N}:vnc";

    public void Save(string key, string userName, string secret)
    {
        var target = Prefix + key;
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobHandle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobHandle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível salvar a credencial no Cofre do Windows.");
            }
        }
        finally
        {
            CryptographicClear(blob);
            blobHandle.Free();
        }
    }

    public string? Read(string key)
    {
        if (!CredRead(Prefix + key, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Não foi possível ler a credencial do Cofre do Windows.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return "";
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Prefix + key, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Não foi possível remover a credencial do Cofre do Windows.");
            }
        }
    }

    private static void CryptographicClear(byte[] data) => Array.Clear(data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public int LastWrittenLow;
        public int LastWrittenHigh;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
