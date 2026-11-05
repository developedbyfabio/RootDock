using System.Text;
using RootDock.Core.LocalShell;

namespace RootDock.Core.Tests;

public class ConPtyConnectionTests
{
    [Fact]
    public async Task Runs_a_local_command_and_closes_when_it_exits()
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var connection = new ConPtyConnection($"\"{cmd}\" /c echo rootdock-ok");
        var output = new StringBuilder();
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DataReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
        };
        connection.Closed += reason => closed.TrySetResult(reason);

        await connection.ConnectAsync(80, 24, TestContext.Current.CancellationToken);
        connection.Start();
        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        Assert.Same(closed.Task, finished);
        Assert.Null(await closed.Task);
        lock (output)
        {
            Assert.Contains("rootdock-ok", output.ToString());
        }
    }

    [Fact]
    public void Finds_at_least_cmd_and_windows_powershell()
    {
        var shells = LocalShells.Available().Select(s => s.Kind).ToList();

        Assert.Contains(RootDock.Core.Sessions.LocalShellKind.Cmd, shells);
        Assert.Contains(RootDock.Core.Sessions.LocalShellKind.PowerShell, shells);
    }
}
