using System.Windows.Threading;
using RootDock.App.Terminal;
using RootDock.Core.Sessions;
using RootDock.Core.Ssh;

namespace RootDock.App.Services;

/// <summary>
/// Túneis das sessões abertas. Cada túnel roda numa só conexão por vez (dividir a tela não tenta
/// abrir a mesma porta duas vezes); se esse terminal fechar, o túnel passa para outro terminal
/// conectado da mesma sessão. Métodos públicos: só na thread de interface.
/// </summary>
public sealed class TunnelService
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<TerminalSession, TunnelSet> _sets = new();

    public TunnelService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Algum túnel mudou de estado (sempre na thread de interface).</summary>
    public event Action? Changed;

    /// <summary>Terminal SSH conectou: abre os túneis automáticos que ainda não estão abertos.</summary>
    public void Attach(TerminalSession session)
    {
        if (session.SshClient is not { } client || _sets.ContainsKey(session))
        {
            return;
        }

        var set = new TunnelSet(client);
        set.Changed += () => _dispatcher.BeginInvoke(() => Changed?.Invoke());
        _sets[session] = set;
        foreach (var definition in session.Entry.Tunnels.Where(t => t.AutoStart))
        {
            if (OwnerOf(session.Entry.Id, definition.Id) is null)
            {
                StartOn(set, definition);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Terminal desconectou ou fechou: leva os túneis abertos para outro terminal da sessão.</summary>
    public void Detach(TerminalSession session)
    {
        if (!_sets.Remove(session, out var set))
        {
            return;
        }

        var open = set.Snapshot().Where(t => t.State == TunnelState.Running).Select(t => t.Definition.Id).ToList();
        _ = Task.Run(set.Dispose);
        if (open.Count > 0 && _sets.FirstOrDefault(p => p.Key.Entry.Id == session.Entry.Id).Value is { } other)
        {
            foreach (var definition in session.Entry.Tunnels.Where(t => open.Contains(t.Id)))
            {
                StartOn(other, definition);
            }
        }

        Changed?.Invoke();
    }

    public bool IsConnected(Guid entryId) => _sets.Keys.Any(s => s.Entry.Id == entryId);

    /// <summary>Estado do túnel na conexão que o tem aberto (ou o último erro).</summary>
    public ActiveTunnel? Status(Guid entryId, Guid tunnelId)
    {
        ActiveTunnel? failed = null;
        foreach (var (session, set) in _sets)
        {
            if (session.Entry.Id != entryId || set.Find(tunnelId) is not { } tunnel)
            {
                continue;
            }

            if (tunnel.State is TunnelState.Running or TunnelState.Starting)
            {
                return tunnel;
            }

            failed ??= tunnel;
        }

        return failed;
    }

    public int RunningCount(Guid entryId) =>
        _sets.Where(p => p.Key.Entry.Id == entryId).Sum(p => p.Value.Snapshot().Count(t => t.State == TunnelState.Running));

    /// <summary>Abre manualmente. Retorna a mensagem de erro, ou null.</summary>
    public string? Start(SessionEntry entry, TunnelDefinition definition)
    {
        if (OwnerOf(entry.Id, definition.Id) is not null)
        {
            return null;
        }

        var set = _sets.FirstOrDefault(p => p.Key.Entry.Id == entry.Id).Value;
        if (set is null)
        {
            return "Conecte a sessão para abrir o túnel.";
        }

        StartOn(set, definition);
        return null;
    }

    public void Stop(Guid entryId, Guid tunnelId)
    {
        foreach (var (session, set) in _sets.Where(p => p.Key.Entry.Id == entryId).ToList())
        {
            _ = Task.Run(() => set.Stop(tunnelId));
        }
    }

    /// <summary>Túnel removido ou alterado na sessão: fecha onde estiver aberto.</summary>
    public void Forget(Guid entryId, Guid tunnelId) => Stop(entryId, tunnelId);

    private TerminalSession? OwnerOf(Guid entryId, Guid tunnelId) =>
        _sets.FirstOrDefault(p => p.Key.Entry.Id == entryId && p.Value.IsRunning(tunnelId)).Key;

    private static void StartOn(TunnelSet set, TunnelDefinition definition) =>
        _ = Task.Run(() =>
        {
            try
            {
                set.Start(definition);
            }
            catch (ObjectDisposedException)
            {
                // A conexão fechou no meio do caminho.
            }
        });
}
