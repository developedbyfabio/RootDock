using System.Diagnostics;

namespace RootDock.Core.MultiExec;

/// <summary>Resultado de um comando num servidor.</summary>
public sealed record CommandResult(int? ExitCode, string Output, string Error);

/// <summary>Executa comandos num servidor já conectado.</summary>
public interface ICommandExecutor : IDisposable
{
    Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct);
}

public enum MultiExecStatus
{
    Waiting,
    Connecting,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Unreachable,
}

/// <summary>Um servidor da execução: como conectar (ou reaproveitar a conexão aberta).</summary>
public sealed record MultiExecTarget(string Id, string Name, Func<CancellationToken, Task<ICommandExecutor>> Connect);

public sealed record MultiExecOptions
{
    /// <summary>Quantos servidores ao mesmo tempo.</summary>
    public int Concurrency { get; init; } = 5;

    /// <summary>Espera entre o início de um servidor e o do próximo (deploy escalonado).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.Zero;

    /// <summary>Tempo máximo do comando em cada servidor.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Tempo máximo para conectar.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Um servidor falhou: os que ainda não começaram são cancelados.</summary>
    public bool StopOnError { get; init; }
}

/// <summary>Andamento de um servidor (enviado a cada mudança de estado).</summary>
public sealed record MultiExecProgress(
    MultiExecTarget Target,
    MultiExecStatus Status,
    CommandResult? Result = null,
    string? Message = null,
    TimeSpan Elapsed = default);

/// <summary>
/// Executa o mesmo comando em vários servidores: N ao mesmo tempo, com intervalo opcional entre
/// eles, tempo limite por servidor, cancelamento e "parar no primeiro erro". Cada servidor tem
/// o seu resultado (saída, erros, código de saída); um servidor fora do ar não atrapalha os outros.
/// </summary>
public static class MultiExecRunner
{
    public static async Task RunAsync(
        IReadOnlyList<MultiExecTarget> targets,
        string command,
        MultiExecOptions options,
        Action<MultiExecProgress> report,
        CancellationToken ct)
    {
        // "Parar no primeiro erro" só impede novos inícios; o que já está rodando termina.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var running = new List<Task>();
        foreach (var target in targets)
        {
            report(new MultiExecProgress(target, MultiExecStatus.Waiting));
        }

        var started = 0;
        try
        {
            foreach (var target in targets)
            {
                if (started > 0 && options.Interval > TimeSpan.Zero)
                {
                    await Task.Delay(options.Interval, stop.Token).ConfigureAwait(false);
                }

                await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                {
                    // O semáforo pode entregar a vaga mesmo depois do pedido de parada (a falha
                    // pede a parada e libera a vaga em seguida): confere de novo antes de começar.
                    gate.Release();
                    break;
                }

                started++;
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        var failed = await RunOneAsync(target, command, options, report, ct).ConfigureAwait(false);
                        if (failed && options.StopOnError)
                        {
                            await stop.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException)
        {
            // cancelado (ou parou no primeiro erro): os que não começaram ficam como cancelados
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        foreach (var target in targets.Skip(started))
        {
            report(new MultiExecProgress(target, MultiExecStatus.Cancelled, Message: "Não executado (cancelado)."));
        }
    }

    /// <summary>true = falhou (para "parar no primeiro erro").</summary>
    private static async Task<bool> RunOneAsync(
        MultiExecTarget target, string command, MultiExecOptions options, Action<MultiExecProgress> report, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (ct.IsCancellationRequested)
        {
            report(new MultiExecProgress(target, MultiExecStatus.Cancelled, Message: "Não executado (cancelado)."));
            return false;
        }

        report(new MultiExecProgress(target, MultiExecStatus.Connecting));
        ICommandExecutor executor;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(options.ConnectTimeout);
            executor = await target.Connect(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report(new MultiExecProgress(target, MultiExecStatus.Cancelled, Message: "Cancelado antes de conectar.", Elapsed: watch.Elapsed));
            return false;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? $"Sem resposta em {options.ConnectTimeout.TotalSeconds:0} s." : ex.Message;
            report(new MultiExecProgress(target, MultiExecStatus.Unreachable, Message: message, Elapsed: watch.Elapsed));
            return true;
        }

        using (executor)
        {
            report(new MultiExecProgress(target, MultiExecStatus.Running, Elapsed: watch.Elapsed));
            try
            {
                var result = await executor.RunAsync(command, options.Timeout, ct).ConfigureAwait(false);
                var ok = result.ExitCode == 0;
                report(new MultiExecProgress(
                    target,
                    ok ? MultiExecStatus.Succeeded : MultiExecStatus.Failed,
                    result,
                    ok ? null : result.ExitCode is { } code ? $"Terminou com código {code}." : "Terminou sem código de saída.",
                    watch.Elapsed));
                return !ok;
            }
            catch (TimeoutException)
            {
                report(new MultiExecProgress(target, MultiExecStatus.TimedOut, Message: $"Passou de {Describe(options.Timeout)}: interrompido.", Elapsed: watch.Elapsed));
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                report(new MultiExecProgress(target, MultiExecStatus.Cancelled, Message: "Interrompido (cancelado).", Elapsed: watch.Elapsed));
                return false;
            }
            catch (Exception ex)
            {
                report(new MultiExecProgress(target, MultiExecStatus.Failed, Message: ex.Message, Elapsed: watch.Elapsed));
                return true;
            }
        }
    }

    private static string Describe(TimeSpan time) =>
        time.TotalMinutes >= 1 && time.Seconds == 0 ? $"{time.TotalMinutes:0} min" : $"{time.TotalSeconds:0} s";
}
