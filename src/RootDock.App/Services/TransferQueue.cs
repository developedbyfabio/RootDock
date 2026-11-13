using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RootDock.App.Services;

public enum TransferState
{
    Waiting,
    Running,
    Done,
    Failed,
    Canceled,

    /// <summary>Pausada pelo usuário: "Retomar" continua (download segue de onde parou).</summary>
    Paused,
}

/// <summary>Uma transferência (arquivo ou pasta inteira) com progresso e cancelamento.</summary>
public sealed partial class TransferItem : ObservableObject
{
    private CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private long _done;

    public TransferItem(string name, bool upload, long totalBytes, string? server = null)
    {
        Name = name;
        IsUpload = upload;
        TotalBytes = totalBytes;
        Server = server;
    }

    public string Name { get; }

    /// <summary>Servidor da transferência (para o histórico).</summary>
    public string? Server { get; }

    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>Quantas vezes rodou (2+ = retomada: pula arquivos já completos).</summary>
    public int Attempt { get; internal set; }

    /// <summary>O cancelamento atual é uma pausa (o arquivo parcial fica para continuar depois).</summary>
    public bool PauseRequested { get; private set; }

    internal Func<TransferItem, Task>? Work { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _partials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registra o destino de um download (o parcial é apagado se a transferência for cancelada).</summary>
    public void TrackPartial(string localPath) => _partials.TryAdd(localPath + RootDock.Core.Sftp.LocalFiles.PartialSuffix, 0);

    /// <summary>Apaga os downloads parciais desta transferência (cancelada ou descartada).</summary>
    internal void DeletePartials()
    {
        foreach (var path in _partials.Keys)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error($"Apagar parcial {path}", ex);
            }
        }

        _partials.Clear();
    }

    internal Action<TransferItem>? ResumeHandler { get; set; }

    public bool CanPause => State is TransferState.Waiting or TransferState.Running;

    public bool CanResume => State is TransferState.Paused or TransferState.Failed;

    public bool IsUpload { get; }

    /// <summary>Símbolo próprio (ex.: "⇄" entre servidores); null = seta de envio ou download.</summary>
    public string? Glyph { get; init; }

    public string DirectionGlyph => Glyph ?? (IsUpload ? "↑" : "↓");

    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _detail = "Na fila";
    [ObservableProperty] private TransferState _state = TransferState.Waiting;

    public bool IsActive => State is TransferState.Waiting or TransferState.Running;

    public CancellationToken Token => _cts.Token;

    /// <summary>Bytes concluídos. Pode ser chamado de qualquer thread; a tela é atualizada pelo temporizador da fila.</summary>
    public void SetDone(long bytes) => Interlocked.Exchange(ref _done, bytes);

    public void AddDone(long bytes) => Interlocked.Add(ref _done, bytes);

    public long Done => Interlocked.Read(ref _done);

    [RelayCommand]
    private void Cancel()
    {
        PauseRequested = false;
        _cts.Cancel();
        if (State == TransferState.Paused)
        {
            Finish(TransferState.Canceled);
            DeletePartials();
        }
    }

    [RelayCommand]
    private void Pause()
    {
        if (!CanPause)
        {
            return;
        }

        PauseRequested = true;
        _cts.Cancel();
    }

    [RelayCommand]
    private void Resume()
    {
        if (CanResume)
        {
            ResumeHandler?.Invoke(this);
        }
    }

    /// <summary>Prepara para rodar de novo (novo cancelamento, mesmo trabalho).</summary>
    internal void Reset()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        PauseRequested = false;
        State = TransferState.Waiting;
        Detail = "Na fila (retomando)";
    }

    internal void Begin()
    {
        State = TransferState.Running;
        _clock.Start();
        Refresh();
    }

    internal void Refresh()
    {
        if (State != TransferState.Running)
        {
            return;
        }

        var done = Done;
        Percent = TotalBytes > 0 ? Math.Min(100, 100.0 * done / TotalBytes) : 0;
        var seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        var speed = done / seconds;
        var remaining = speed > 1 && TotalBytes > done ? TimeSpan.FromSeconds((TotalBytes - done) / speed) : TimeSpan.Zero;
        Detail = $"{Format.Bytes(done)} de {Format.Bytes(TotalBytes)}  ·  {Format.Speed(speed)}" +
                 (remaining > TimeSpan.FromSeconds(1) ? $"  ·  {remaining:mm\\:ss}" : "");
    }

    internal void Finish(TransferState state, string? message = null)
    {
        _clock.Stop();
        State = state;
        Percent = state == TransferState.Done ? 100 : Percent;
        Detail = state switch
        {
            TransferState.Done => $"Concluído  ·  {Format.Bytes(TotalBytes)} em {_clock.Elapsed.TotalSeconds:0.0} s",
            TransferState.Canceled => "Cancelado",
            TransferState.Paused => $"Pausado em {Format.Bytes(Done)} de {Format.Bytes(TotalBytes)}  ·  Retomar continua",
            _ => message ?? "Falhou",
        };
        OnPropertyChanged(nameof(IsActive));
    }

    partial void OnStateChanged(TransferState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }
}

/// <summary>Fila de transferências do aplicativo: até 3 simultâneas, progresso atualizado 4x por segundo.</summary>
public sealed partial class TransferQueue : ObservableObject
{
    private readonly SemaphoreSlim _slots = new(3);
    private readonly DispatcherTimer _timer;

    public TransferQueue(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    public ObservableCollection<TransferItem> Items { get; } = [];

    [ObservableProperty] private bool _hasItems;

    public event Action<TransferItem>? Completed;

    /// <summary>Enfileira o trabalho. <paramref name="work"/> informa o progresso via <see cref="TransferItem.SetDone"/>.</summary>
    public TransferItem Enqueue(string name, bool upload, long totalBytes, Func<TransferItem, Task> work, string? server = null, string? glyph = null)
    {
        var item = new TransferItem(name, upload, totalBytes, server) { Work = work, ResumeHandler = Resume, Glyph = glyph };
        Items.Insert(0, item);
        HasItems = true;
        _timer.Start();
        _ = RunAsync(item, work);
        return item;
    }

    /// <summary>Fila pausada: nada novo começa; as em andamento são pausadas.</summary>
    [ObservableProperty] private bool _isPaused;

    private TaskCompletionSource _unpaused = CreateOpen();

    private static TaskCompletionSource CreateOpen()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    [RelayCommand]
    private void PauseAll()
    {
        if (IsPaused)
        {
            return;
        }

        IsPaused = true;
        _unpaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var item in Items.Where(i => i.State == TransferState.Running).ToList())
        {
            item.PauseCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ResumeAll()
    {
        IsPaused = false;
        _unpaused.TrySetResult();
        foreach (var item in Items.Where(i => i.State == TransferState.Paused).ToList())
        {
            Resume(item);
        }
    }

    private void Resume(TransferItem item)
    {
        if (item.Work is null || !item.CanResume)
        {
            return;
        }

        item.Reset();
        _timer.Start();
        _ = RunAsync(item, item.Work);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsActive).ToList())
        {
            if (item.State == TransferState.Paused)
            {
                item.DeletePartials(); // pausada e descartada: o parcial não serve mais
            }

            Items.Remove(item);
        }

        HasItems = Items.Count > 0;
    }

    private async Task RunAsync(TransferItem item, Func<TransferItem, Task> work)
    {
        try
        {
            // Pegou a vaga mas a fila foi pausada enquanto esperava: devolve e espera retomar.
            while (true)
            {
                await _unpaused.Task.WaitAsync(item.Token);
                await _slots.WaitAsync(item.Token);
                if (!IsPaused)
                {
                    break;
                }

                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            item.Finish(item.PauseRequested ? TransferState.Paused : TransferState.Canceled);
            if (!item.PauseRequested)
            {
                Completed?.Invoke(item);
            }

            return;
        }

        try
        {
            item.Attempt++;
            item.Begin();
            await work(item);
            item.Finish(TransferState.Done);
        }
        catch (OperationCanceledException)
        {
            item.Finish(item.PauseRequested ? TransferState.Paused : TransferState.Canceled);
            if (!item.PauseRequested)
            {
                item.DeletePartials();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Transferência {item.Name}", ex);
            item.Finish(TransferState.Failed, ex.Message);
        }
        finally
        {
            _slots.Release();
        }

        if (item.State == TransferState.Paused)
        {
            return; // continua na lista para "Retomar"
        }

        Completed?.Invoke(item);
        if (item.State == TransferState.Done)
        {
            // Concluídas somem sozinhas; falhas ficam até o usuário limpar.
            await Task.Delay(TimeSpan.FromSeconds(6));
            Items.Remove(item);
            HasItems = Items.Count > 0;
        }
    }

    private void Tick()
    {
        var running = false;
        foreach (var item in Items)
        {
            if (item.State == TransferState.Running)
            {
                item.Refresh();
                running = true;
            }
        }

        if (!running && Items.All(i => !i.IsActive))
        {
            _timer.Stop();
        }
    }
}
