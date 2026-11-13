using System.Text.RegularExpressions;
using RootDock.Core.Sftp;

namespace RootDock.Core.ServerTransfer;

/// <summary>
/// Comandos do modo direto: o servidor de origem roda o rsync falando com o de destino por SSH,
/// com uma chave criada só para isso. Tudo aqui é texto de shell POSIX; nada é executado.
/// </summary>
public static partial class DirectScripts
{
    /// <summary>Marca das chaves temporárias no authorized_keys do destino.</summary>
    public const string CommentPrefix = "rootdock-transfer-";

    private const string KeyOptions = "no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty";

    [GeneratedRegex(@"^/[A-Za-z0-9_./-]*rootdock-xfer\.[A-Za-z0-9]+$")]
    private static partial Regex WorkDirectory();

    [GeneratedRegex(@"^ssh-ed25519 [A-Za-z0-9+/]+=*$")]
    private static partial Regex PublicKey();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex UserName();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]+$")]
    private static partial Regex HostName();

    public static bool IsWorkDirectory(string path) => WorkDirectory().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    public static bool IsUserName(string user) => UserName().IsMatch(user);

    public static bool IsHostName(string host) => HostName().IsMatch(host);

    /// <summary>"ssh-ed25519 AAAA... comentário" → "ssh-ed25519 AAAA..." (null se não for uma chave ed25519).</summary>
    public static string? CleanPublicKey(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = parts.Length >= 2 ? parts[0] + " " + parts[1] : "";
        return PublicKey().IsMatch(key) ? key : null;
    }

    /// <summary>Comentário da chave: a data (UTC) permite limpar o que sobrou de um RootDock fechado à força.</summary>
    public static string Comment(DateTime utcNow, string id) => $"{CommentPrefix}{utcNow:yyyyMMddHHmm}-{id}";

    /// <summary>Roda o texto num sh POSIX, qualquer que seja o shell de login do usuário (fish, csh...).</summary>
    public static string Wrap(string script) => "sh -c " + Quote(script);

    /// <summary>Origem: confere rsync, ssh e ssh-keygen e mostra a versão do rsync.</summary>
    public static string ProbeSource() =>
        "command -v rsync >/dev/null 2>&1 || { echo NO-RSYNC; exit 0; }; " +
        "command -v ssh >/dev/null 2>&1 || { echo NO-SSH; exit 0; }; " +
        "command -v ssh-keygen >/dev/null 2>&1 || { echo NO-KEYGEN; exit 0; }; " +
        "LC_ALL=C rsync --version 2>/dev/null | head -n 1";

    public static string ProbeTarget() => "command -v rsync >/dev/null 2>&1 && echo OK || echo NO-RSYNC";

    /// <summary>Origem: pasta de trabalho só do usuário e o par de chaves. Imprime a pasta e a chave pública.</summary>
    public static string CreateKey(string comment) =>
        "set -e; umask 077; d=$(mktemp -d \"${TMPDIR:-/tmp}/rootdock-xfer.XXXXXXXX\"); " +
        $"ssh-keygen -q -t ed25519 -N '' -C {Quote(comment)} -f \"$d/key\" >/dev/null; " +
        "printf '%s\\n' \"$d\"; cat \"$d/key.pub\"";

    /// <summary>Origem: a chave do servidor de destino que o RootDock já conferiu (a origem não confia em mais nada).</summary>
    public static string WriteKnownHosts(string directory, IReadOnlyList<string> lines) =>
        "umask 077; printf '%s\\n' " + string.Join(" ", lines.Select(Quote)) + " > " + Quote(directory + "/known_hosts");

    /// <summary>
    /// Destino: autoriza a chave temporária (sem terminal nem redirecionamentos) e tira as que
    /// sobraram de antes de <paramref name="staleBefore"/> (formato yyyyMMddHHmm).
    /// </summary>
    public static string Authorize(string publicKey, string comment, string staleBefore) =>
        "set -e; umask 077; mkdir -p \"$HOME/.ssh\"; f=\"$HOME/.ssh/authorized_keys\"; touch \"$f\"; t=\"$f.rootdock-tmp\"; " +
        $"if grep -q {Quote(CommentPrefix)} \"$f\"; then " +
        $"if awk -v c={Quote(staleBefore)} '{{ n = $NF; if (n ~ /^{CommentPrefix}[0-9]+-/) {{ split(n, p, \"-\"); if (p[3] < c) next }} print }}' \"$f\" > \"$t\"; then cat \"$t\" > \"$f\"; fi; " +
        "rm -f \"$t\"; fi; " +
        "if [ -s \"$f\" ] && [ -n \"$(tail -c 1 \"$f\")\" ]; then echo >> \"$f\"; fi; " +
        $"printf '%s\\n' {Quote($"{KeyOptions} {publicKey} {comment}")} >> \"$f\"";

    /// <summary>Destino: tira a chave temporária. Só regrava o arquivo se o filtro rodou sem erro.</summary>
    public static string Revoke(string comment) =>
        "f=\"$HOME/.ssh/authorized_keys\"; t=\"$f.rootdock-tmp\"; [ -f \"$f\" ] || exit 0; umask 077; " +
        $"grep -v -F -- {Quote(comment)} \"$f\" > \"$t\"; rc=$?; " +
        "if [ \"$rc\" -le 1 ]; then cat \"$t\" > \"$f\"; fi; rm -f \"$t\"";

    /// <summary>O ssh que a origem usa para chegar no destino: só a chave temporária e só a chave de servidor conferida.</summary>
    public static string SshCommand(string directory, int port) =>
        $"ssh -i {Quote(directory + "/key")} -p {port} -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes " +
        $"-o UserKnownHostsFile={Quote(directory + "/known_hosts")} -o GlobalKnownHostsFile=/dev/null " +
        "-o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=4 -o Compression=no";

    /// <summary>Origem: testa a conexão com o destino (sai com 0 se entrou).</summary>
    public static string Test(string directory, string user, string host, int port) =>
        $"{SshCommand(directory, port)} {Quote(user + "@" + host)} true";

    /// <summary>Origem: tamanho total (bytes) dos itens, para a barra de progresso. Vazio se o du não tiver -b.</summary>
    public static string TotalSize(string sourceRoot, IEnumerable<string> relativePaths) =>
        $"cd {Quote(sourceRoot)} && du -sbc -- {string.Join(" ", relativePaths.Select(p => Quote("./" + p)))} 2>/dev/null | tail -n 1 | cut -f 1";

    /// <summary>
    /// Origem: o rsync. A lista de itens (separados por NUL) fica em <paramref name="listFile"/>;
    /// o que está pela metade vai para uma pasta à parte no destino e só entra no lugar completo.
    /// </summary>
    public static string Rsync(
        string directory, string pidFile, string listFile, string sourceRoot, string targetRoot,
        string user, string host, int port, bool preserve, bool skipExisting)
    {
        var flags = preserve ? "-rlptgoD" : "-rl";
        if (skipExisting)
        {
            flags += " --ignore-existing";
        }

        var remote = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var destination = $"{user}@{remote}:{targetRoot.TrimEnd('/')}/";
        return $"cd {Quote(sourceRoot)} && echo $$ > {Quote(pidFile)} && LC_ALL=C exec rsync {flags} -s --info=progress2 --no-inc-recursive " +
               $"--partial-dir=.rootdock-partial --from0 --files-from={Quote(listFile)} -e {Quote(SshCommand(directory, port))} ./ {Quote(destination)}";
    }

    /// <summary>Origem: interrompe o rsync em andamento (pausa ou cancelamento).</summary>
    public static string Kill(string pidFile) =>
        $"p=$(cat {Quote(pidFile)} 2>/dev/null); [ -n \"$p\" ] && kill \"$p\" 2>/dev/null; rm -f {Quote(pidFile)}; true";

    /// <summary>Origem: apaga a pasta de trabalho (chave privada incluída).</summary>
    public static string RemoveWorkDirectory(string directory) =>
        IsWorkDirectory(directory)
            ? $"rm -rf -- {Quote(directory)}"
            : throw new ArgumentException("Pasta de trabalho inesperada.", nameof(directory));

    private static string Quote(string text) => RemotePath.ShellQuote(text);
}
