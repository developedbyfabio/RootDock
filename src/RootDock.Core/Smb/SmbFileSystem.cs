using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RootDock.Core.Sftp;

namespace RootDock.Core.Smb;

/// <summary>Login recusado ou sem permissão no servidor Windows (para pedir outro usuário).</summary>
public sealed class SmbAccessException(string message, int code) : UnauthorizedAccessException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Arquivos de um servidor Windows pelo compartilhamento de arquivos do próprio Windows (SMB,
/// porta 445): unidades administrativas (C$, D$) e pastas compartilhadas. O gerenciador usa
/// caminhos no formato "/C$/Users/joao"; aqui eles viram "\\servidor\C$\Users\joao".
/// Quando o login é de outro usuário (não o do Windows atual), abre uma conexão própria
/// (como o "net use"), desfeita ao fechar.
/// </summary>
public sealed class SmbFileSystem : IFileTransport
{
    private const int ConnectTemporary = 4;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;

    /// <summary>Conexões abertas por nós (desfeitas ao fechar, ou ao sair do RootDock).</summary>
    private static readonly Dictionary<string, int> Owned = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _ownsConnection;
    private int _disposed;

    static SmbFileSystem() => AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseOwnedConnections();

    private SmbFileSystem(string host, bool ownsConnection, string home)
    {
        Host = host;
        _ownsConnection = ownsConnection;
        HomeDirectory = home;
    }

    public string Host { get; }

    public string HomeDirectory { get; }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0;

    public string Protocol => "SMB";

    /// <summary>
    /// Conecta ao servidor. Usuário diferente do Windows atual (ou login do Windows recusado):
    /// usa <paramref name="user"/>/<paramref name="password"/>.
    /// </summary>
    public static Task<SmbFileSystem> ConnectAsync(string host, string? user, string? password, CancellationToken ct) =>
        Task.Run(() => Connect(NormalizeHost(host), user, password), ct);

    private static SmbFileSystem Connect(string host, string? user, string? password)
    {
        var hasLogin = !string.IsNullOrWhiteSpace(user) && password is not null;
        var owns = false;
        if (hasLogin && !IsCurrentWindowsUser(user!))
        {
            owns = AddConnection(host, user!, password!);
        }

        var code = EnumShares(host, out var shares);
        if ((code is ErrorAccessDenied or ErrorLogonFailure or ErrorInvalidPassword) && hasLogin && !owns)
        {
            // O login do Windows atual não serve neste servidor: tenta com o da sessão.
            owns = AddConnection(host, user!, password!);
            code = EnumShares(host, out shares);
        }

        if (code != 0)
        {
            if (owns)
            {
                CancelConnection(host);
            }

            throw Explain(code, host);
        }

        // "Pasta pessoal": o perfil do usuário no servidor (C:\Users\usuario), se existir.
        var home = "/";
        if (shares.Any(s => string.Equals(s.Name, "C$", StringComparison.OrdinalIgnoreCase)))
        {
            var login = user is { Length: > 0 } ? user.Trim() : Environment.UserName;
            var leaf = login.Contains('\\') ? login[(login.IndexOf('\\') + 1)..] : login.Split('@')[0];
            var profile = $@"\\{host}\C$\Users\{leaf}";
            if (leaf.Length > 0 && SafeDirectoryExists(profile))
            {
                home = $"/C$/Users/{leaf}";
            }
        }

        return new SmbFileSystem(host, owns, home);
    }

    // ------------------------------------------------------------------ caminhos

    /// <summary>"/C$/pasta" → "\\servidor\C$\pasta" ("/" é a lista de compartilhamentos).</summary>
    public string ToUnc(string path)
    {
        var relative = path.Trim('/').Replace('/', '\\');
        if (relative.Split('\\').Any(part => part is ".." or "."))
        {
            throw new ArgumentException($"Caminho inválido: {path}");
        }

        return relative.Length == 0 ? $@"\\{Host}" : $@"\\{Host}\{relative}";
    }

    /// <summary>
    /// O que a pessoa digitou → caminho do gerenciador: "\\servidor\C$\pasta", "C:\pasta" (unidade
    /// do servidor) ou "/C$/pasta". Null se for de outro servidor ou não for um caminho.
    /// </summary>
    public static string? FromWindowsPath(string host, string text)
    {
        text = text.Trim();
        if (text.StartsWith('/'))
        {
            return text.Length > 1 ? text.TrimEnd('/') : text;
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = text[2..];
            var slash = rest.IndexOf('\\');
            var server = slash < 0 ? rest : rest[..slash];
            if (!string.Equals(server, host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var tail = slash < 0 ? "" : rest[(slash + 1)..].Replace('\\', '/').Trim('/');
            return "/" + tail;
        }

        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] is '\\' or '/'))
        {
            var tail = text.Length > 3 ? text[3..].Replace('\\', '/').Trim('/') : "";
            return ($"/{char.ToUpperInvariant(text[0])}$/" + tail).TrimEnd('/');
        }

        return null;
    }

    private static bool IsRoot(string path) => path.Trim('/').Length == 0;

    private static bool IsShareRoot(string path) => path.Trim('/') is { Length: > 0 } p && !p.Contains('/');

    private static string NormalizeHost(string host)
    {
        host = host.Trim().TrimStart('\\').TrimEnd('\\');
        if (host.Length == 0 || host.IndexOfAny(['\\', '/', ' ']) >= 0)
        {
            throw new ArgumentException($"Servidor inválido: {host}");
        }

        return host;
    }

    // ------------------------------------------------------------------ operações

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run<IReadOnlyList<RemoteEntry>>(() =>
    {
        if (IsRoot(path))
        {
            var code = EnumShares(Host, out var shares);
            if (code != 0)
            {
                throw Explain(code, Host);
            }

            return shares.Select(s => new RemoteEntry(s.Name, "/" + s.Name, true, false, 0, default, Describe(s), 0, 0)).ToList();
        }

        var directory = new DirectoryInfo(ToUnc(path));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        return directory.EnumerateFileSystemInfos("*", options).Select(i => ToEntry(path, i)).ToList();
    }, ct);

    public Task<RemoteEntry> GetAsync(string path, CancellationToken ct) => Run(() =>
    {
        if (IsRoot(path))
        {
            return new RemoteEntry(Host, "/", true, false, 0, default, "", 0, 0);
        }

        var unc = ToUnc(path);
        if (IsShareRoot(path))
        {
            var share = path.Trim('/');
            return Directory.Exists(unc)
                ? new RemoteEntry(share, "/" + share, true, false, 0, default, "", 0, 0)
                : throw new DirectoryNotFoundException($"Compartilhamento não encontrado: {share}");
        }

        FileSystemInfo info = File.Exists(unc) ? new FileInfo(unc) : Directory.Exists(unc) ? new DirectoryInfo(unc)
            : throw new FileNotFoundException($"Não encontrado no servidor: {path}");
        return ToEntry(RemotePath.Parent(path), info);
    }, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Run(() =>
    {
        if (IsRoot(path))
        {
            return true;
        }

        var unc = ToUnc(path);
        return File.Exists(unc) || Directory.Exists(unc);
    }, ct);

    public Task<string> DownloadFileAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct,
        Func<bool>? keepPartial = null, bool resume = false, bool overwrite = false) => RunAsync(async () =>
    {
        var source = ToUnc(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var temp = localPath + LocalFiles.PartialSuffix;
        try
        {
            var resumeFrom = resume && File.Exists(temp) ? new FileInfo(temp).Length : 0;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
            await using (var output = new FileStream(temp, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                if (resumeFrom > input.Length)
                {
                    resumeFrom = 0;
                    output.SetLength(0);
                }

                input.Seek(resumeFrom, SeekOrigin.Begin);
                await CopyAsync(input, output, resumeFrom, progress, ct).ConfigureAwait(false);
            }

            var saved = LocalFiles.Commit(temp, localPath, overwrite);
            TrySetTime(saved, File.GetLastWriteTimeUtc(source));
            return saved;
        }
        catch (OperationCanceledException) when (keepPartial?.Invoke() == true)
        {
            throw; // pausa: o parcial fica para continuar
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct) => RunAsync(async () =>
    {
        if (IsRoot(remotePath) || IsShareRoot(remotePath))
        {
            throw new IOException("Abra uma unidade ou pasta compartilhada antes de enviar.");
        }

        var target = ToUnc(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Grava num nome temporário e troca no fim: uma falha no meio não estraga o arquivo que já existe.
        var temp = target + LocalFiles.PartialSuffix;
        try
        {
            await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await CopyAsync(input, output, 0, progress, ct).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
            TrySetTime(target, File.GetLastWriteTimeUtc(localPath));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(() =>
    {
        if (IsRoot(path) || IsShareRoot(path))
        {
            throw new IOException("Abra uma unidade ou pasta compartilhada antes de criar pastas.");
        }

        Directory.CreateDirectory(ToUnc(path));
        return true;
    }, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(() =>
    {
        if (IsRoot(from) || IsShareRoot(from))
        {
            throw new IOException("Unidades e compartilhamentos não podem ser renomeados por aqui.");
        }

        var source = ToUnc(from);
        var target = ToUnc(to);
        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
        {
            throw new IOException($"Já existe \"{RemotePath.Name(to)}\" nesta pasta.");
        }

        var isDirectory = Directory.Exists(source);
        void Move(string a, string b)
        {
            if (isDirectory)
            {
                Directory.Move(a, b);
            }
            else
            {
                File.Move(a, b);
            }
        }

        if (caseOnly)
        {
            // Só maiúsculas/minúsculas ("foto.JPG" → "foto.jpg"): o Windows exige passar por outro nome.
            var step = source + ".rootdock-" + Guid.NewGuid().ToString("N")[..6];
            Move(source, step);
            Move(step, target);
        }
        else
        {
            Move(source, target);
        }

        return true;
    }, ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => Run(() =>
    {
        if (IsRoot(entry.FullPath) || IsShareRoot(entry.FullPath))
        {
            throw new IOException("Unidades e compartilhamentos não podem ser excluídos.");
        }

        var unc = ToUnc(entry.FullPath);
        if (Directory.Exists(unc))
        {
            DeleteTree(new DirectoryInfo(unc), ct);
        }
        else if (File.Exists(unc))
        {
            ClearReadOnly(new FileInfo(unc));
            File.Delete(unc);
        }

        return true;
    }, ct);

    // ------------------------------------------------------------------ apoio

    private static async Task CopyAsync(Stream input, Stream output, long start, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 17];
        var done = start;
        var lastReport = Environment.TickCount64;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (progress is not null && Environment.TickCount64 - lastReport > 150)
            {
                lastReport = Environment.TickCount64;
                progress.Report(done);
            }
        }

        progress?.Report(done);
    }

    private static void DeleteTree(DirectoryInfo directory, CancellationToken ct)
    {
        // Links (junções) são removidos sem entrar neles: nunca apaga o que fica do outro lado.
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete();
            return;
        }

        foreach (var item in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, ReturnSpecialDirectories = false }))
        {
            ct.ThrowIfCancellationRequested();
            if (item is DirectoryInfo sub)
            {
                DeleteTree(sub, ct);
            }
            else
            {
                ClearReadOnly(item);
                item.Delete();
            }
        }

        directory.Attributes &= ~FileAttributes.ReadOnly;
        directory.Delete();
    }

    private static void ClearReadOnly(FileSystemInfo item)
    {
        if (item.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            item.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static RemoteEntry ToEntry(string parent, FileSystemInfo info)
    {
        var attributes = new List<string>(3);
        if (info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            attributes.Add("oculto");
        }

        if (info.Attributes.HasFlag(FileAttributes.System))
        {
            attributes.Add("sistema");
        }

        if (info.Attributes.HasFlag(FileAttributes.ReadOnly) && info is FileInfo)
        {
            attributes.Add("somente leitura");
        }

        return new RemoteEntry(
            info.Name,
            RemotePath.Combine(parent.Length == 0 ? "/" : parent, info.Name),
            info is DirectoryInfo,
            info.Attributes.HasFlag(FileAttributes.ReparsePoint),
            info is FileInfo file ? file.Length : 0,
            info.LastWriteTime,
            string.Join(" · ", attributes),
            0,
            0);
    }

    private static string Describe(Share share) =>
        share.Name.Length == 2 && share.Name[1] == '$' && char.IsLetter(share.Name[0])
            ? $"Unidade {char.ToUpperInvariant(share.Name[0])}:"
            : "Compartilhada";

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TrySetTime(string path, DateTime utc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, utc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // horário é detalhe: o arquivo foi copiado
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Task<T> Run<T>(Func<T> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(!IsConnected, this);
        return Task.Run(action, ct);
    }

    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(!IsConnected, this);
        return Task.Run(action, ct);
    }

    private Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(!IsConnected, this);
        return Task.Run(action, ct);
    }

    /// <summary>O login informado é o mesmo do Windows atual (então a conexão dele já serve).</summary>
    private static bool IsCurrentWindowsUser(string user)
    {
        var current = WindowsIdentity.GetCurrent().Name; // DOMINIO\usuario
        if (user.Contains('\\'))
        {
            return string.Equals(user, current, StringComparison.OrdinalIgnoreCase);
        }

        var name = user.Split('@')[0];
        return string.Equals(name, Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    private static Exception Explain(int code, string host) => code switch
    {
        ErrorAccessDenied => new SmbAccessException(
            $"Acesso negado em \\\\{host}. Para ver as unidades (C$, D$) o usuário precisa ser administrador do servidor.", code),
        ErrorLogonFailure or ErrorInvalidPassword or 1327 or 1330 or 1331 or 1909 => new SmbAccessException(
            $"O servidor recusou o login: {new Win32Exception(code).Message}", code),
        ErrorCredentialConflict => new SmbAccessException(
            $"O Windows já está conectado a \\\\{host} com outro usuário. Use o mesmo usuário, ou desconecte o outro (net use \\\\{host}\\IPC$ /delete).", code),
        ErrorBadNetPath or 51 or 1203 or 1231 => new IOException(
            $"Não foi possível chegar aos arquivos de \\\\{host}: a porta 445 (compartilhamento de arquivos do Windows) pode estar bloqueada no firewall, ou o compartilhamento está desligado no servidor."),
        _ => new IOException($"\\\\{host}: {new Win32Exception(code).Message} (código {code})."),
    };

    // ------------------------------------------------------------------ Windows (mpr / netapi32)

    private readonly record struct Share(string Name, string Remark);

    private static int EnumShares(string host, out List<Share> shares)
    {
        shares = [];
        var resume = 0;
        var code = NetShareEnum($@"\\{host}", 1, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (code != 0)
            {
                return code;
            }

            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * size));
                var isDisk = (info.Type & 0xFF) == 0;
                var name = info.NetName ?? "";
                if (isDisk && name.Length > 0 && !string.Equals(name, "ADMIN$", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "print$", StringComparison.OrdinalIgnoreCase))
                {
                    shares.Add(new Share(name, info.Remark ?? ""));
                }
            }

            // Unidades (C$, D$...) primeiro, depois as pastas compartilhadas.
            shares = shares.OrderBy(s => s.Name.EndsWith('$') ? 0 : 1).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>true = conexão nova (nossa); false = já havia uma (de outro login) e ela é usada.</summary>
    private static bool AddConnection(string host, string user, string password)
    {
        var resource = new NetResource { RemoteName = $@"\\{host}\IPC$" };
        var code = WNetAddConnection2(resource, password, user, ConnectTemporary);
        if (code == ErrorCredentialConflict)
        {
            return false;
        }

        if (code != 0)
        {
            throw Explain(code, host);
        }

        lock (Owned)
        {
            Owned[host] = Owned.GetValueOrDefault(host) + 1;
        }

        return true;
    }

    private static void CancelConnection(string host)
    {
        lock (Owned)
        {
            if (!Owned.TryGetValue(host, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                Owned.Remove(host);
            }
            else
            {
                Owned[host] = count - 1;
            }
        }

        _ = WNetCancelConnection2($@"\\{host}\IPC$", 0, false);
    }

    private static void CloseOwnedConnections()
    {
        List<KeyValuePair<string, int>> all;
        lock (Owned)
        {
            all = [.. Owned];
            Owned.Clear();
        }

        foreach (var (host, count) in all)
        {
            for (var i = 0; i < count; i++)
            {
                _ = WNetCancelConnection2($@"\\{host}\IPC$", 0, true);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsConnection)
        {
            CancelConnection(Host);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string? NetName;
        public uint Type;
        public string? Remark;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NetResource resource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
