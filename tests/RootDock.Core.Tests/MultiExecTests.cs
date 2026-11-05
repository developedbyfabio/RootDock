using System.Collections.Concurrent;
using RootDock.Core.MultiExec;

namespace RootDock.Core.Tests;

public sealed class MultiExecTests
{
    [Theory]
    [InlineData("rm -rf /var/www/app")]
    [InlineData("sudo rm -rf /")]
    [InlineData("rm -fr *")]
    [InlineData("mkfs.ext4 /dev/sdb1")]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M")]
    [InlineData("sudo reboot")]
    [InlineData("shutdown -h now")]
    [InlineData("init 0")]
    [InlineData("chmod -R 777 /var/www")]
    [InlineData("mysql -e 'DROP DATABASE loja'")]
    [InlineData("psql -c \"DELETE FROM pedidos;\"")]
    [InlineData("mysql -e \"UPDATE clientes SET ativo=0\"")]
    [InlineData("iptables -F")]
    [InlineData("systemctl stop sshd")]
    [InlineData("userdel -r joao")]
    [InlineData("sudo passwd root")]
    [InlineData("git push --force origin main")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("docker system prune -af")]
    [InlineData("crontab -r")]
    [InlineData("php artisan migrate:fresh --seed")]
    [InlineData("echo x > /etc/passwd")]
    [InlineData("apt-get -y purge nginx")]
    [InlineData(":(){ :|:& };:")]
    public void DangerousCommands_AreFlagged(string command) =>
        Assert.NotEmpty(CommandSafety.Analyze(command));

    [Theory]
    [InlineData("df -h")]
    [InlineData("uptime && free -m")]
    [InlineData("systemctl status nginx")]
    [InlineData("systemctl restart nginx")]
    [InlineData("tail -n 100 /var/log/syslog")]
    [InlineData("rm /tmp/arquivo.lock")]
    [InlineData("mysql -e \"DELETE FROM sessoes WHERE expira < NOW()\"")]
    [InlineData("mysql -e \"UPDATE clientes SET ativo=0 WHERE id=5\"")]
    [InlineData("git pull --ff-only")]
    [InlineData("docker ps -a")]
    [InlineData("apt-get update && apt-get -y upgrade")]
    [InlineData("cat /etc/passwd | wc -l")]
    [InlineData("crontab -l")]
    public void EverydayCommands_AreNotFlagged(string command) =>
        Assert.Empty(CommandSafety.Analyze(command));

    private sealed class FakeExecutor(Func<string, CancellationToken, Task<CommandResult>> run, Action? disposed = null) : ICommandExecutor
    {
        public Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct) => run(command, ct);

        public void Dispose() => disposed?.Invoke();
    }

    private static MultiExecTarget Target(string name, Func<string, CancellationToken, Task<CommandResult>> run, Action? disposed = null) =>
        new(name, name, _ => Task.FromResult<ICommandExecutor>(new FakeExecutor(run, disposed)));

    private static Dictionary<string, MultiExecProgress> Final(ConcurrentQueue<MultiExecProgress> events) =>
        events.GroupBy(e => e.Target.Id).ToDictionary(g => g.Key, g => g.Last());

    [Fact]
    public async Task EachServerGetsItsOwnResult()
    {
        var events = new ConcurrentQueue<MultiExecProgress>();
        var disposed = 0;
        var targets = new[]
        {
            Target("web1", (_, _) => Task.FromResult(new CommandResult(0, "ok\n", "")), () => Interlocked.Increment(ref disposed)),
            Target("web2", (_, _) => Task.FromResult(new CommandResult(2, "", "falhou\n")), () => Interlocked.Increment(ref disposed)),
            new MultiExecTarget("db1", "db1", _ => throw new InvalidOperationException("Sem rota até o servidor")),
        };

        await MultiExecRunner.RunAsync(targets, "uptime", new MultiExecOptions(), events.Enqueue, TestContext.Current.CancellationToken);

        var final = Final(events);
        Assert.Equal(MultiExecStatus.Succeeded, final["web1"].Status);
        Assert.Equal("ok\n", final["web1"].Result!.Output);
        Assert.Equal(MultiExecStatus.Failed, final["web2"].Status);
        Assert.Contains("2", final["web2"].Message);
        Assert.Equal(MultiExecStatus.Unreachable, final["db1"].Status);
        Assert.Contains("Sem rota", final["db1"].Message);
        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task Concurrency_IsRespected()
    {
        var current = 0;
        var peak = 0;
        var targets = Enumerable.Range(1, 8).Select(i => Target($"s{i}", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(60, ct);
            Interlocked.Decrement(ref current);
            return new CommandResult(0, "", "");
        })).ToList();

        await MultiExecRunner.RunAsync(targets, "x", new MultiExecOptions { Concurrency = 3 }, _ => { }, TestContext.Current.CancellationToken);

        Assert.InRange(peak, 1, 3);
    }

    [Fact]
    public async Task Timeout_IsReportedPerServer()
    {
        var events = new ConcurrentQueue<MultiExecProgress>();
        var targets = new[] { Target("lento", (_, _) => throw new TimeoutException()) };

        await MultiExecRunner.RunAsync(targets, "sleep 999", new MultiExecOptions { Timeout = TimeSpan.FromSeconds(5) }, events.Enqueue, TestContext.Current.CancellationToken);

        Assert.Equal(MultiExecStatus.TimedOut, Final(events)["lento"].Status);
    }

    [Fact]
    public async Task StopOnError_SkipsServersThatHaveNotStarted()
    {
        var events = new ConcurrentQueue<MultiExecProgress>();
        var targets = new[]
        {
            Target("a", (_, _) => Task.FromResult(new CommandResult(1, "", "erro"))),
            Target("b", (_, _) => Task.FromResult(new CommandResult(0, "", ""))),
            Target("c", (_, _) => Task.FromResult(new CommandResult(0, "", ""))),
        };

        await MultiExecRunner.RunAsync(targets, "deploy", new MultiExecOptions { Concurrency = 1, StopOnError = true }, events.Enqueue, TestContext.Current.CancellationToken);

        var final = Final(events);
        Assert.Equal(MultiExecStatus.Failed, final["a"].Status);
        Assert.Equal(MultiExecStatus.Cancelled, final["b"].Status);
        Assert.Equal(MultiExecStatus.Cancelled, final["c"].Status);
    }

    [Fact]
    public async Task Cancel_StopsRunningAndPending()
    {
        var events = new ConcurrentQueue<MultiExecProgress>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var targets = Enumerable.Range(1, 4).Select(i => Target($"s{i}", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new CommandResult(0, "", "");
        })).ToList();

        var run = MultiExecRunner.RunAsync(targets, "x", new MultiExecOptions { Concurrency = 2 }, events.Enqueue, cts.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.All(Final(events).Values, p => Assert.Equal(MultiExecStatus.Cancelled, p.Status));
    }

    [Fact]
    public async Task Interval_SpacesTheStarts()
    {
        var starts = new ConcurrentQueue<DateTime>();
        var targets = Enumerable.Range(1, 3).Select(i => Target($"s{i}", (_, _) =>
        {
            starts.Enqueue(DateTime.UtcNow);
            return Task.FromResult(new CommandResult(0, "", ""));
        })).ToList();

        await MultiExecRunner.RunAsync(targets, "x", new MultiExecOptions { Concurrency = 10, Interval = TimeSpan.FromMilliseconds(150) }, _ => { }, TestContext.Current.CancellationToken);

        var list = starts.OrderBy(t => t).ToList();
        Assert.True(list[2] - list[0] >= TimeSpan.FromMilliseconds(250), $"intervalo total {(list[2] - list[0]).TotalMilliseconds} ms");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
