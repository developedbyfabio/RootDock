using System.Threading.Channels;

namespace RootDock.Core.Terminal;

/// <summary>
/// Fila de saída com um único consumidor: mantém a ordem entre digitação e redimensionamento
/// e tira a escrita de rede/pipe da thread de interface.
/// </summary>
internal sealed class OutboundQueue : IDisposable
{
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;

    public void Start(Action<byte[]> write, Action<int, int> resize, Action<Exception> onError)
    {
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    if (item.Data is not null)
                    {
                        write(item.Data);
                    }
                    else
                    {
                        resize(item.Columns, item.Rows);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        });
    }

    public void EnqueueData(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            _channel.Writer.TryWrite(new Item(data.ToArray(), 0, 0));
        }
    }

    public void EnqueueResize(int columns, int rows) =>
        _channel.Writer.TryWrite(new Item(null, columns, rows));

    public void Dispose()
    {
        // O CancellationTokenSource não é descartado de propósito: a bomba pode ainda estar iniciando
        // e ler o Token; sem timer associado, não há recurso a liberar.
        _channel.Writer.TryComplete();
        _stop.Cancel();
    }

    private readonly record struct Item(byte[]? Data, int Columns, int Rows);
}
