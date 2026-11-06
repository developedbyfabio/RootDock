namespace RootDock.Core.Macros;

/// <summary>Terminal onde a macro digita (implementado pela sessão aberta no RootDock).</summary>
public interface IMacroTerminal
{
    /// <summary>Digita o texto como se fosse no teclado ("\r" = Enter).</summary>
    void Type(string text);

    bool IsConnected { get; }
}

/// <summary>Andamento: passo atual (1 a N) e o que está acontecendo.</summary>
public sealed record MacroProgress(int Step, int Total, MacroStep Current, string Status, bool Waiting);

/// <summary>A macro parou: terminal caiu ou um texto esperado não apareceu.</summary>
public sealed class MacroFailedException(string message) : Exception(message);

/// <summary>
/// Executa uma macro passo a passo. Cada comando é digitado e, com <see cref="CommandMacro.WaitForPrompt"/>,
/// o próximo só sai quando o prompt do shell volta (saída parada e a linha atual com cara de prompt,
/// ou igual ao prompt de antes de começar). Comando seguido de "esperar texto" não espera o prompt:
/// é o caso de perguntas como "password:" ou "[Y/n]". Uma espera longa pode ser pulada
/// (<see cref="SkipWait"/>) ou a macro parada pelo token.
/// </summary>
public sealed class MacroRunner(CommandMacro macro, IMacroTerminal terminal, TerminalOutputWatcher watcher, string commandPrefix = "")
{
    public const double DefaultExpectSeconds = 30;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TypeAhead = TimeSpan.FromMilliseconds(150);
    private int _skip;

    /// <summary>Tempo sem saída para considerar que o prompt voltou de vez (não é só um pedaço dele).</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(300);

    public event Action<MacroProgress>? Progress;

    /// <summary>Passos que de fato fazem algo (comando vazio, pausa zero etc. são ignorados).</summary>
    public static IReadOnlyList<MacroStep> Runnable(CommandMacro macro) => macro.Steps.Where(s => s.Kind switch
    {
        MacroStepKind.Command => !string.IsNullOrWhiteSpace(s.Text),
        MacroStepKind.Wait => s.Seconds > 0,
        MacroStepKind.WaitFor => !string.IsNullOrWhiteSpace(s.Text),
        _ => false,
    }).ToList();

    /// <summary>Pula a espera atual (prompt ou texto) e segue para o próximo passo.</summary>
    public void SkipWait() => Interlocked.Exchange(ref _skip, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        var steps = Runnable(macro);
        var home = watcher.CurrentLine.TrimEnd(); // prompt antes de começar (reconhece prompts sem # $ >)
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();
            Interlocked.Exchange(ref _skip, 0);
            var step = steps[i];
            switch (step.Kind)
            {
                case MacroStepKind.Command:
                    Report(i, steps.Count, step, "executando", waiting: false);
                    watcher.Mark();
                    terminal.Type(commandPrefix + step.Text.Replace("\r\n", "\n").Replace('\n', '\r') + "\r");
                    var answersPrompt = i + 1 < steps.Count && steps[i + 1].Kind == MacroStepKind.WaitFor;
                    if (macro.WaitForPrompt && !answersPrompt)
                    {
                        Report(i, steps.Count, step, "aguardando o comando terminar", waiting: true);
                        await WaitUntilAsync(() => watcher.LinesSinceMark > 0 && watcher.IdleFor >= SettleTime &&
                            (watcher.AtPrompt || (home.Length > 0 && watcher.CurrentLine.TrimEnd() == home)), null, ct).ConfigureAwait(true);
                    }
                    else
                    {
                        await Task.Delay(TypeAhead, ct).ConfigureAwait(true);
                    }

                    break;

                case MacroStepKind.Wait:
                    Report(i, steps.Count, step, $"pausa de {MacroStep.Format(step.Seconds)} s", waiting: true);
                    await WaitUntilAsync(() => false, TimeSpan.FromSeconds(step.Seconds), ct, timeoutIsSuccess: true).ConfigureAwait(true);
                    break;

                case MacroStepKind.WaitFor:
                    var seconds = step.Seconds > 0 ? step.Seconds : DefaultExpectSeconds;
                    Report(i, steps.Count, step, $"esperando aparecer \"{step.Text}\"", waiting: true);
                    // O texto vale desde o comando anterior (a pergunta costuma chegar logo depois dele).
                    await WaitUntilAsync(() => watcher.SawSinceMark(step.Text), TimeSpan.FromSeconds(seconds), ct,
                        failure: $"\"{step.Text}\" não apareceu em {MacroStep.Format(seconds)} s.").ConfigureAwait(true);
                    break;
            }
        }
    }

    private async Task WaitUntilAsync(Func<bool> done, TimeSpan? timeout, CancellationToken ct, string? failure = null, bool timeoutIsSuccess = false)
    {
        var started = DateTime.UtcNow;
        while (!done())
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();
            if (Interlocked.Exchange(ref _skip, 0) == 1)
            {
                return;
            }

            if (timeout is { } limit && DateTime.UtcNow - started >= limit)
            {
                if (timeoutIsSuccess)
                {
                    return;
                }

                throw new MacroFailedException(failure ?? "Tempo esgotado.");
            }

            await Task.Delay(Poll, ct).ConfigureAwait(true);
        }
    }

    private void EnsureConnected()
    {
        if (!terminal.IsConnected)
        {
            throw new MacroFailedException("O terminal foi desconectado.");
        }
    }

    private void Report(int index, int total, MacroStep step, string status, bool waiting) =>
        Progress?.Invoke(new MacroProgress(index + 1, total, step, status, waiting));
}
