using System.Diagnostics;
using System.Text;

namespace RootDock.App.Services;

/// <summary>
/// Tempos da inicialização gravados uma vez no log (processo → .NET → janela → WebView2 → página pronta).
/// Serve para medir e comparar versões sem ferramenta externa: "Inicialização (ms): ...".
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<(string Name, long At)> Marks = [];
    private static double _runtimeMs = -1;
    private static bool _done;

    /// <summary>Chamado na primeira linha do Main: quanto o Windows e o .NET levaram até aqui.</summary>
    public static void Begin()
    {
        try
        {
            _runtimeMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - Clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception)
        {
            _runtimeMs = -1; // sem permissão para ler o próprio processo: segue sem esse número
        }
    }

    public static void Mark(string name)
    {
        if (!_done)
        {
            Marks.Add((name, Clock.ElapsedMilliseconds));
        }
    }

    /// <summary>Último marco: grava a linha no log e para de medir.</summary>
    public static void Complete(string name)
    {
        if (_done)
        {
            return;
        }

        Mark(name);
        _done = true;
        var line = new StringBuilder("Inicialização (ms): ");
        if (_runtimeMs >= 0)
        {
            line.Append("processo→Main ").Append((int)_runtimeMs).Append(" · ");
        }

        line.AppendJoin(" · ", Marks.Select(m => $"{m.Name} {m.At}"));
        line.Append($" · memória {Environment.WorkingSet / (1024 * 1024)} MB");
        Log.Info(line.ToString());
    }
}
