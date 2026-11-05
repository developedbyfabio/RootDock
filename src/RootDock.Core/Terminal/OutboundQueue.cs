using System.Threading.Channels;

namespace RootDock.Core.Terminal;

/// <summary>
/// Fila de saída com um único consumidor: mantém a ordem entre digitação e redimensionamento
/// e tira a escrita de rede/pipe da thread de interface.
/// </summary>
internal sealed class OutboundQueue : IDisposable
{
    /// <summary>Colagens grandes vão em pedaços: o Ctrl+C consegue passar na frente do resto.</summary>
    private const int ChunkSize = 16 * 1024;

    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _epoch;
    private long _pendingBytes; // texto na fila (o canal de leitor único não informa Count)

    /// <summary>Só uma colagem grande pendente é descartada pelo Ctrl+C; teclas digitadas rápido nunca.</summary>
    private const int DropThreshold = 4096;

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
                        Interlocked.Add(ref _pendingBytes, -item.Data.Length);
                        // Texto enfileirado antes de um Ctrl+C é descartado (como o terminal faz com a
                        // entrada pendente ao interromper): colar 20 MB por engano não prende a sessão.
                        if (item.Epoch == Volatile.Read(ref _epoch))
                        {
                            write(item.Data);
                        }
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
        if (data.IsEmpty)
        {
            return;
        }

        if (data.Length == 1 && data[0] == 0x03 && Interlocked.Read(ref _pendingBytes) > DropThreshold)
        {
            Interlocked.Increment(ref _epoch); // Ctrl+C com envio pendente: o que estava na fila sai
        }

        var epoch = Volatile.Read(ref _epoch);
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            var chunk = data.Slice(offset, Math.Min(ChunkSize, data.Length - offset));
            Interlocked.Add(ref _pendingBytes, chunk.Length);
            if (!_channel.Writer.TryWrite(new Item(chunk.ToArray(), 0, 0, epoch)))
            {
                Interlocked.Add(ref _pendingBytes, -chunk.Length); // fila já encerrada
            }
        }
    }

    public void EnqueueResize(int columns, int rows) =>
        _channel.Writer.TryWrite(new Item(null, columns, rows, 0));

    public void Dispose()
    {
        // O CancellationTokenSource não é descartado de propósito: a bomba pode ainda estar iniciando
        // e ler o Token; sem timer associado, não há recurso a liberar.
        _channel.Writer.TryComplete();
        _stop.Cancel();
    }

    private readonly record struct Item(byte[]? Data, int Columns, int Rows, int Epoch);
}
