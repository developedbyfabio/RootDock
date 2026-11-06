using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RootDock.Core.Macros;
using RootDock.Core.Serialization;
using RootDock.Core.Settings;

namespace RootDock.Core.Tests;

/// <summary>Macros: leitura da saída do terminal (prompt, textos esperados) e execução passo a passo.</summary>
public class MacroTests
{
    private const string BashPrompt = "\u001b[01;32mroot@srv\u001b[00m:\u001b[01;34m~\u001b[00m# ";

    // ------------------------------------------------------------------ leitura da saída

    [Theory]
    [InlineData(BashPrompt)]
    [InlineData("fabio@dev:~/projetos$ ")]
    [InlineData("PS C:\\Users\\fabio> ")]
    [InlineData("C:\\Windows\\System32>")]
    [InlineData("\u001b]0;root@srv: ~\u0007root@srv:~# ")] // título da janela (OSC) antes do prompt
    public void Recognizes_shell_prompts(string output)
    {
        var watcher = new TerminalOutputWatcher();
        watcher.Append(output);
        Assert.True(watcher.AtPrompt, watcher.CurrentLine);
    }

    [Theory]
    [InlineData("Do you want to continue? [Y/n] ")]
    [InlineData("[sudo] password for root: ")]
    [InlineData("root@srv:~# apt update")] // comando digitado, ainda sem Enter
    [InlineData("Lendo listas de pacotes... 45%")]
    public void Questions_and_typed_commands_are_not_a_prompt(string output)
    {
        var watcher = new TerminalOutputWatcher();
        watcher.Append(output);
        Assert.False(watcher.AtPrompt, watcher.CurrentLine);
    }

    [Fact]
    public void Escape_sequence_cut_between_chunks_does_not_leak_into_the_text()
    {
        var watcher = new TerminalOutputWatcher();
        watcher.Append("\u001b[01;3");
        watcher.Append("2mroot@srv\u001b]0;tít");
        watcher.Append("ulo\u001b\\:~# ");
        Assert.Equal("root@srv:~# ", watcher.CurrentLine);
        Assert.True(watcher.AtPrompt);
    }

    [Fact]
    public void Carriage_return_rewrites_the_line_like_a_progress_bar()
    {
        var watcher = new TerminalOutputWatcher();
        watcher.Append("baixando 10%\rbaixando 80%\r\u001b[Kroot@srv:~# ");
        Assert.Equal("root@srv:~# ", watcher.CurrentLine);
    }

    [Fact]
    public void Text_and_lines_count_only_after_the_mark()
    {
        var watcher = new TerminalOutputWatcher();
        watcher.Append("antes: PASSWORD\r\n");
        watcher.Mark();
        Assert.False(watcher.SawSinceMark("password"));
        Assert.Equal(0, watcher.LinesSinceMark);
        watcher.Append("ls\r\n\u001b[1mPassword\u001b[0m: ");
        Assert.True(watcher.SawSinceMark("password:"));
        Assert.Equal(1, watcher.LinesSinceMark);
    }

    // ------------------------------------------------------------------ execução

    [Fact]
    public async Task Each_command_waits_for_the_prompt_of_the_previous_one()
    {
        var shell = new FakeShell();
        shell.Reply["lento"] = ("pronto\r\n", 400);
        var macro = Macro(MacroStep.Command("lento"), MacroStep.Command("rapido"));

        await Runner(macro, shell).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["lento\r", "rapido\r"], shell.Typed.Select(t => t.Text));
        var promptAfterSlow = shell.Prompts[1]; // 0 = prompt inicial
        Assert.True(shell.Typed[1].At >= promptAfterSlow, "o segundo comando saiu antes de o primeiro terminar");
    }

    [Fact]
    public async Task Command_followed_by_expect_answers_the_question()
    {
        var shell = new FakeShell();
        shell.Reply["sudo -v"] = ("[sudo] password for root: ", -1); // fica na pergunta, sem prompt
        var macro = Macro(MacroStep.Command("sudo -v"), MacroStep.Expect("password for"), MacroStep.Command("segredo"));

        await Runner(macro, shell).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["sudo -v\r", "segredo\r"], shell.Typed.Select(t => t.Text));
    }

    [Fact]
    public async Task Expected_text_that_never_appears_fails_with_a_clear_message()
    {
        var shell = new FakeShell();
        var macro = Macro(MacroStep.Command("ls"), MacroStep.Expect("password", timeoutSeconds: 0.3), MacroStep.Command("nunca"));

        var ex = await Assert.ThrowsAsync<MacroFailedException>(() => Runner(macro, shell).RunAsync(TestContext.Current.CancellationToken));

        Assert.Contains("\"password\" não apareceu", ex.Message);
        Assert.DoesNotContain(shell.Typed, t => t.Text == "nunca\r");
    }

    [Fact]
    public async Task Disconnected_terminal_stops_the_macro()
    {
        var shell = new FakeShell();
        shell.Reply["trava"] = ("", -1);
        var macro = Macro(MacroStep.Command("trava"), MacroStep.Command("depois"));
        var run = Runner(macro, shell).RunAsync(TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        shell.IsConnected = false;

        var ex = await Assert.ThrowsAsync<MacroFailedException>(() => run);
        Assert.Contains("desconectado", ex.Message);
        Assert.Single(shell.Typed);
    }

    [Fact]
    public async Task Cancel_stops_before_the_next_command()
    {
        var shell = new FakeShell();
        shell.Reply["trava"] = ("", -1);
        using var cts = new CancellationTokenSource();
        var run = Runner(Macro(MacroStep.Command("trava"), MacroStep.Command("depois")), shell).RunAsync(cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(shell.Typed);
    }

    [Fact]
    public async Task Skip_wait_moves_on_when_the_prompt_never_comes_back()
    {
        var shell = new FakeShell();
        shell.Reply["interativo"] = ("Continuar? ", -1);
        var runner = Runner(Macro(MacroStep.Command("interativo"), MacroStep.Command("seguinte")), shell);
        var run = runner.RunAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(shell.Typed);

        runner.SkipWait();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["interativo\r", "seguinte\r"], shell.Typed.Select(t => t.Text));
    }

    [Fact]
    public async Task Pause_waits_and_progress_reports_every_step()
    {
        var shell = new FakeShell();
        var macro = Macro(MacroStep.Command("a"), MacroStep.Pause(0.3), MacroStep.Command("b"), MacroStep.Command("  "));
        var runner = Runner(macro, shell);
        var steps = new List<int>();
        runner.Progress += p => steps.Add(p.Step);
        var watch = Stopwatch.StartNew();

        await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(watch.ElapsedMilliseconds >= 300);
        Assert.Equal([1, 2, 3], steps.Distinct()); // o comando vazio é ignorado
        Assert.Equal(["a\r", "b\r"], shell.Typed.Select(t => t.Text));
    }

    [Fact]
    public void Macros_are_saved_with_the_settings_readably()
    {
        var settings = new AppSettings();
        settings.Macros.Add(Macro(MacroStep.Command("apt update"), MacroStep.Expect("[Y/n]", 60), MacroStep.Command("y"), MacroStep.Pause(2)));

        var json = JsonSerializer.Serialize(settings, RootDockJson.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, RootDockJson.Options)!;

        Assert.Contains("\"WaitFor\"", json);
        var macro = Assert.Single(back.Macros);
        Assert.Equal(4, macro.Steps.Count);
        Assert.Equal(MacroStepKind.WaitFor, macro.Steps[1].Kind);
        Assert.Equal(60, macro.Steps[1].Seconds);
        Assert.Equal("apt update\n[esperar \"[Y/n]\"]\ny\n[pausa de 2 s]", macro.Preview);
    }

    [Fact]
    public void Destructive_commands_are_flagged()
    {
        Assert.True(Macro(MacroStep.Command("ls"), MacroStep.Command("rm -rf /tmp/x")).LooksDestructive);
        Assert.False(Macro(MacroStep.Command("ls -la"), MacroStep.Command("df -h")).LooksDestructive);
    }

    private static CommandMacro Macro(params MacroStep[] steps) => new() { Name = "teste", Steps = [.. steps] };

    private static MacroRunner Runner(CommandMacro macro, FakeShell shell) =>
        new(macro, shell, shell.Watcher) { SettleTime = TimeSpan.FromMilliseconds(80) };

    /// <summary>Shell de mentira: ecoa o comando, responde e mostra o prompt (ou fica na pergunta).</summary>
    private sealed class FakeShell : IMacroTerminal
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public FakeShell()
        {
            Prompt();
        }

        public TerminalOutputWatcher Watcher { get; } = new();

        /// <summary>Resposta por comando: saída e atraso até o prompt (-1 = não volta ao prompt).</summary>
        public Dictionary<string, (string Output, int DelayMs)> Reply { get; } = [];

        public ConcurrentQueue<(string Text, long At)> TypedQueue { get; } = new();

        public List<(string Text, long At)> Typed => [.. TypedQueue];

        public List<long> Prompts { get; } = [];

        public bool IsConnected { get; set; } = true;

        public void Type(string text)
        {
            TypedQueue.Enqueue((text, _clock.ElapsedMilliseconds));
            var command = text.TrimEnd('\r');
            Watcher.Append(command + "\r\n");
            var (output, delay) = Reply.GetValueOrDefault(command, ("ok\r\n", 20));
            _ = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(delay, 0));
                Watcher.Append(output);
                if (delay >= 0)
                {
                    Prompt();
                }
            });
        }

        private void Prompt()
        {
            lock (Prompts)
            {
                Prompts.Add(_clock.ElapsedMilliseconds);
            }

            Watcher.Append(BashPrompt);
        }
    }
}
