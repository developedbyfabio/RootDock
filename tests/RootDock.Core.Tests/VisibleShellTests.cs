using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RootDock.Core.Assistant;

namespace RootDock.Core.Tests;

/// <summary>Modo "mostrar no terminal" do assistente: marcas do shell e acompanhamento dos comandos.</summary>
public sealed class VisibleShellTests
{
    private static string Token(VisibleShell shell) => Regex.Match(shell.Script(), @"7777;([0-9a-f]+);D").Groups[1].Value;

    private static string Done(VisibleShell shell, int exit) => $"\u001b]7777;{Token(shell)};D;{exit}\u0007";

    private static string Start(VisibleShell shell) => $"\u001b]7777;{Token(shell)};C\u0007";

    [Fact]
    public async Task Run_CapturesOutputBetweenMarks_EvenSplitAcrossPackets()
    {
        var typed = new List<string>();
        var shell = new VisibleShell(typed.Add);
        shell.Feed(Done(shell, 0)); // prompt inicial
        Assert.True(shell.AtPrompt);

        var run = shell.RunAsync("df -h /", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("\u0015df -h /\r", typed.Single());
        Assert.False(shell.AtPrompt);

        var stream = "df -h /\r\n" + Start(shell) + "\u001b[1mFilesystem\u001b[0m  Size\r\n/dev/sda1   50G\r\n" + Done(shell, 0) + "root@srv:~# ";
        foreach (var piece in stream.Chunk(7))
        {
            shell.Feed(piece); // as marcas chegam cortadas no meio
        }

        var result = await run;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Filesystem  Size\n/dev/sda1   50G\n", result.Output);
        Assert.True(shell.AtPrompt);
    }

    [Fact]
    public async Task Run_OldShellWithoutStartMark_DropsTheEchoLine()
    {
        var shell = new VisibleShell(_ => { });
        shell.Feed(Done(shell, 0));
        var run = shell.RunAsync("false", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        shell.Feed("false\r\nsaida\r\n" + Done(shell, 1));
        var result = await run;
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("saida\n", result.Output);
    }

    [Fact]
    public async Task Run_IgnoresMarksWithAnotherToken()
    {
        var shell = new VisibleShell(_ => { });
        shell.Feed(Done(shell, 0));
        var run = shell.RunAsync("cat arquivo", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // Um arquivo com uma marca falsa dentro não encerra o comando.
        shell.Feed("cat arquivo\r\n" + Start(shell) + "antes\u001b]7777;0000000000000000;D;0\u0007depois\r\n");
        Assert.False(run.IsCompleted);
        shell.Feed(Done(shell, 7));
        var result = await run;
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("antes", result.Output);
        Assert.Contains("depois", result.Output);
    }

    [Fact]
    public async Task Run_NotAtPromptOrMultiline_IsRefusedWithoutTyping()
    {
        var typed = new List<string>();
        var shell = new VisibleShell(typed.Add);
        await Assert.ThrowsAsync<VisibleShellUnavailableException>(() => shell.RunAsync("ls", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        shell.Feed(Done(shell, 0));
        await Assert.ThrowsAsync<VisibleShellUnavailableException>(() => shell.RunAsync("a\nb", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Empty(typed);
    }

    [Fact]
    public async Task Run_Cancelled_SendsCtrlC()
    {
        var typed = new List<string>();
        var shell = new VisibleShell(typed.Add);
        shell.Feed(Done(shell, 0));
        using var cancel = new CancellationTokenSource();
        var run = shell.RunAsync("sleep 600", Timeout.InfiniteTimeSpan, cancel.Token);
        shell.Feed("sleep 600\r\n" + Start(shell));
        cancel.Cancel();
        shell.Feed("^C\r\n" + Done(shell, 130));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("\u0003", typed.Last());
        Assert.True(shell.AtPrompt);
    }

    [Fact]
    public async Task Run_ShellChangedUnderneath_ReportsInsteadOfHanging()
    {
        var shell = new VisibleShell(_ => { });
        shell.Feed(Start(shell) + Done(shell, 0)); // este shell marca o começo dos comandos
        // Nada volta (a pessoa entrou num "su" sem integração): em 8 s o comando é dado como não acompanhado.
        var error = await Assert.ThrowsAsync<IOException>(() => shell.RunAsync("ls", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken));
        Assert.Contains("read_terminal", error.Message);
    }

    /// <summary>O trecho de integração num bash de verdade (Git Bash): marcas, saída, código de retorno e estado que persiste.</summary>
    [Fact]
    public async Task RealBash_ActivatesAndFollowsCommands()
    {
        var bash = new[] { @"C:\Program Files\Git\usr\bin\bash.exe", @"C:\Program Files\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);
        if (bash is null)
        {
            Assert.Skip("Git Bash não encontrado.");
            return;
        }

        var info = new ProcessStartInfo(bash)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec bash --norc --noprofile -i 2>&1"); // prompt e saída no mesmo canal, em ordem
        info.Environment["PROMPT_COMMAND"] = "true"; // já existe um PROMPT_COMMAND: o nosso entra na frente
        using var process = Process.Start(info)!;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Sem terminal de verdade não há Ctrl+U nem eco: digita só o texto, com Enter como nova linha.
            var shell = new VisibleShell(text =>
            {
                process.StandardInput.Write(text.Replace("\u0015", "").Replace("\r", "\n"));
                process.StandardInput.Flush();
            });
            _ = Task.Run(async () =>
            {
                var buffer = new char[4096];
                int read;
                while ((read = await process.StandardOutput.ReadAsync(buffer, ct)) > 0)
                {
                    shell.Feed(buffer.AsSpan(0, read));
                }

                shell.Close();
            }, ct);

            var script = Path.Combine(Path.GetTempPath(), "rootdock-shell-test-" + Guid.NewGuid().ToString("N")[..8]);
            await File.WriteAllTextAsync(script, shell.Script(), new UTF8Encoding(false), ct);
            try
            {
                Assert.True(await shell.ActivateAsync(script.Replace('\\', '/'), TimeSpan.FromSeconds(20), ct));
                Assert.True(shell.AtPrompt);

                var first = await shell.RunAsync("echo um; echo dois; (exit 3)", TimeSpan.FromSeconds(20), ct);
                Assert.Equal(3, first.ExitCode);
                Assert.Contains("um\ndois", first.Output);

                // É o mesmo shell: pasta e variáveis ficam de um comando para o outro.
                Assert.Equal(0, (await shell.RunAsync("cd /tmp && RD_TESTE=valor-42", TimeSpan.FromSeconds(20), ct)).ExitCode);
                var second = await shell.RunAsync("pwd; echo $RD_TESTE; echo \"$PROMPT_COMMAND\"", TimeSpan.FromSeconds(20), ct);
                Assert.Contains("/tmp\nvalor-42", second.Output);
                Assert.Contains("__rootdock_done; true", second.Output);

                // Carregar de novo (outro RootDock, outra conversa) não duplica nada.
                Assert.True(await shell.ActivateAsync(script.Replace('\\', '/'), TimeSpan.FromSeconds(20), ct));
                var third = await shell.RunAsync("echo \"$PROMPT_COMMAND\"", TimeSpan.FromSeconds(20), ct);
                Assert.Contains("__rootdock_done; true", third.Output);
                Assert.DoesNotContain("__rootdock_done; __rootdock_done", third.Output);
            }
            finally
            {
                File.Delete(script);
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }
}
