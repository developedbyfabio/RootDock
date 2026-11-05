using RootDock.Core.Terminal;

namespace RootDock.Core.Tests;

/// <summary>Fila de envio do terminal: colagem grande em pedaços e Ctrl+C passando na frente.</summary>
public class OutboundQueueTests
{
    [Fact]
    public async Task Ctrl_c_drops_the_rest_of_a_large_paste()
    {
        using var queue = new OutboundQueue();
        var written = new List<byte[]>();
        using var gate = new ManualResetEventSlim(false);
        var interrupted = new TaskCompletionSource();
        queue.Start(
            data =>
            {
                gate.Wait(); // servidor lento: a fila acumula
                lock (written)
                {
                    written.Add(data);
                }

                if (data is [0x03])
                {
                    interrupted.TrySetResult();
                }
            },
            (_, _) => { },
            _ => { });

        queue.EnqueueData(new byte[200 * 1024]); // 13 pedaços de 16 KB
        await Task.Delay(100, TestContext.Current.CancellationToken);
        queue.EnqueueData([0x03]);
        gate.Set();
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        lock (written)
        {
            Assert.True(written.Count <= 2, $"esperado no máximo o pedaço em andamento + Ctrl+C, veio {written.Count}");
            Assert.Equal([0x03], written[^1]);
            Assert.All(written.Take(written.Count - 1), chunk => Assert.True(chunk.Length <= 16 * 1024));
        }
    }

    [Fact]
    public async Task Normal_typing_is_kept_in_order()
    {
        using var queue = new OutboundQueue();
        var written = new List<byte>();
        var done = new TaskCompletionSource();
        queue.Start(data => { lock (written) { written.AddRange(data); if (written.Count == 4) done.TrySetResult(); } }, (_, _) => { }, _ => { });

        queue.EnqueueData("ls"u8);
        queue.EnqueueData([0x03]); // sem nada pendente: só mais uma tecla
        queue.EnqueueData("\r"u8);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("ls\u0003\r"u8.ToArray(), written);
    }
}
