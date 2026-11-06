using System.Text;
using Renci.SshNet;

namespace RootDock.Core.Monitoring;

/// <summary>
/// Acompanha CPU, memória, rede, discos e a pasta atual do terminal usando um único canal
/// na mesma conexão SSH do terminal (sem nova autenticação). O script só lê /proc e comandos de consulta.
/// </summary>
public sealed class ServerMonitor : IDisposable
{
    /// <summary>
    /// Script POSIX executado no servidor. Sem aspas simples (vai dentro de sh -c '...') e sem "!"
    /// (evita expansão de histórico em csh). A pasta do terminal é descoberta pelo shell "irmão":
    /// todos os canais de uma conexão SSH são filhos do mesmo processo sshd ($PPID).
    /// </summary>
    internal const string Script = """
        export LC_ALL=C
        P=$PPID
        first=1
        while :; do
          echo "@@BEGIN"
          if [ "$first" = 1 ]; then
            echo "@host $(hostname 2>/dev/null)"
            if [ -r /etc/os-release ]; then . /etc/os-release; echo "@os $PRETTY_NAME"; echo "@osid $ID $ID_LIKE"; fi
            echo "@kernel $(uname -sr 2>/dev/null)"
            echo "@cores $(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo 2>/dev/null)"
            echo "@ips $(hostname -I 2>/dev/null)"
            first=0
          fi
          u=$(id -un 2>/dev/null)
          echo "@user $u"
          echo "@users $(who 2>/dev/null | wc -l)"
          echo "@usersame $(who 2>/dev/null | grep -c "^$u ")"
          head -n 1 /proc/stat 2>/dev/null | sed "s/^/@cpu /"
          echo "@load $(cat /proc/loadavg 2>/dev/null)"
          echo "@uptime $(cat /proc/uptime 2>/dev/null)"
          grep -E "^(MemTotal|MemAvailable|SwapTotal|SwapFree):" /proc/meminfo 2>/dev/null | sed "s/^/@mem /"
          tail -n +3 /proc/net/dev 2>/dev/null | sed "s/^/@net /"
          df -P -k -x tmpfs -x devtmpfs -x overlay -x squashfs -x efivarfs 2>/dev/null | tail -n +2 | sed "s/^/@df /"
          ps -eo pid=,user=,pcpu=,pmem=,comm= --sort=-pcpu 2>/dev/null | head -n 5 | sed "s/^/@proc /"
          for c in $(cat /proc/$P/task/*/children 2>/dev/null); do
            [ "$c" = "$$" ] && continue
            t=$(readlink /proc/$c/fd/0 2>/dev/null)
            case "$t" in
              /dev/pts/*)
                g=$(sed "s/.*) //" /proc/$c/stat 2>/dev/null | cut -d " " -f 6)
                d=$(readlink /proc/$g/cwd 2>/dev/null || readlink /proc/$c/cwd 2>/dev/null)
                [ -n "$d" ] && echo "@cwd $d"
                f=$(cat /proc/$g/comm 2>/dev/null)
                [ -n "$f" ] && echo "@fg $f"
                ;;
            esac
          done
          echo "@@END"
          sleep 2
        done
        """;

    private readonly SshClient _client;
    private readonly ServerSnapshotParser _parser = new();
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    public ServerMonitor(SshClient client)
    {
        _client = client;
    }

    /// <summary>Nova leitura (a cada ~2 s). Disparado em thread de fundo.</summary>
    public event Action<ServerSnapshot>? SnapshotReceived;

    /// <summary>O monitor parou (servidor sem /proc, canal recusado, conexão caiu).</summary>
    public event Action<string>? Stopped;

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "server-monitor" }.Start();
    }

    /// <summary>
    /// Para o monitor sem bloquear: o cancelamento fecha o canal no servidor e a própria
    /// thread do monitor descarta o comando ao sair da leitura.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
    }

    internal static string CommandText => "exec sh -c '" + Script.Replace("\r", "") + "'";

    private void Run()
    {
        string reason = "Monitor encerrado.";
        try
        {
            using var command = _client.CreateCommand(CommandText);
            _ = command.ExecuteAsync(_stop.Token).ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            using var reader = new StreamReader(command.OutputStream, Encoding.UTF8);
            var block = new List<string>(64);
            var received = 0;
            while (!_stop.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (line == "@@BEGIN")
                {
                    block.Clear();
                }
                else if (line == "@@END")
                {
                    received++;
                    SnapshotReceived?.Invoke(_parser.Parse(block));
                }
                else
                {
                    block.Add(line);
                }
            }

            if (received == 0)
            {
                reason = "O servidor não forneceu dados de monitoramento.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = ex.Message;
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            Stopped?.Invoke(reason);
        }
    }
}
