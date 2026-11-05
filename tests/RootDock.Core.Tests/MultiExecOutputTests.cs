using RootDock.Core.MultiExec;
using RootDock.Core.Security;
using RootDock.Core.Ssh;

namespace RootDock.Core.Tests;

/// <summary>Saída da execução em vários servidores com teto de memória.</summary>
public class MultiExecOutputTests
{
    [Fact]
    public void Bounded_text_keeps_head_and_tail_and_counts_the_rest()
    {
        var text = new BoundedText(headChars: 5, tailChars: 4);
        text.Append("abcde");
        text.Append("0123456789");
        text.Append("WXYZ");

        var result = text.ToString();

        Assert.StartsWith("abcde", result);
        Assert.EndsWith("WXYZ", result);
        Assert.Contains("10 caracteres omitidos", result);
    }

    [Fact]
    public void Small_output_is_untouched()
    {
        var text = new BoundedText(headChars: 100, tailChars: 10);
        text.Append("tudo certo\n");

        Assert.Equal("tudo certo\n", text.ToString());
    }

    /// <summary>
    /// Contra um servidor SSH de verdade (só com ROOTDOCK_SSH_TEST=1). Variáveis: ROOTDOCK_SSH_HOST,
    /// ROOTDOCK_SSH_PORT, ROOTDOCK_SSH_USER, ROOTDOCK_SSH_KEY, ROOTDOCK_SSH_KNOWN_HOSTS e
    /// ROOTDOCK_SSH_BIG_COMMAND (comando que gera muitos MB de saída).
    /// </summary>
    [Fact]
    public async Task Huge_output_from_a_real_server_stays_bounded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ROOTDOCK_SSH_TEST") == "1", "Servidor SSH de teste não configurado.");
        var ct = TestContext.Current.CancellationToken;
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
        var knownHosts = new KnownHostsStore(Env("ROOTDOCK_SSH_KNOWN_HOSTS"));
        using var executor = await SshCommandExecutor.ConnectAsync(
            Env("ROOTDOCK_SSH_HOST"), int.Parse(Env("ROOTDOCK_SSH_PORT")),
            new SshCredentials(Env("ROOTDOCK_SSH_USER"), null, Env("ROOTDOCK_SSH_KEY"), null), knownHosts, ct);

        var small = await executor.RunAsync("echo rootdock-ok", TimeSpan.FromSeconds(30), ct);
        Assert.Contains("rootdock-ok", small.Output);
        Assert.Equal(0, small.ExitCode);

        var big = await executor.RunAsync(Env("ROOTDOCK_SSH_BIG_COMMAND"), TimeSpan.FromSeconds(120), ct);
        Assert.Contains("omitidos", big.Output);
        Assert.True(big.Output.Length < SshCommandExecutor.OutputHeadChars + SshCommandExecutor.OutputTailChars + 200,
            $"saída guardada com {big.Output.Length:N0} caracteres");
    }
}
