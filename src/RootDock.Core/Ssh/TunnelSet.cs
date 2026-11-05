using Renci.SshNet;
using RootDock.Core.Sessions;

namespace RootDock.Core.Ssh;

public enum TunnelState
{
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>Estado de um túnel numa conexão.</summary>
public sealed class ActiveTunnel(TunnelDefinition definition)
{
    public TunnelDefinition Definition { get; } = definition;

    public TunnelState State { get; internal set; } = TunnelState.Stopped;

    /// <summary>Falha ao abrir (porta ocupada, servidor recusou) ou último erro de uma conexão.</summary>
    public string? Error { get; internal set; }

    internal ForwardedPort? Port { get; set; }
}

/// <summary>
/// Túneis abertos numa conexão SSH (o mesmo cliente do terminal, sem nova autenticação).
/// Abrir e fechar esperam o servidor; chame fora da thread de interface.
/// </summary>
public sealed class TunnelSet : IDisposable
{
    private readonly SshClient _client;
    private readonly Dictionary<Guid, ActiveTunnel> _tunnels = new();
    private readonly object _gate = new();
    private bool _disposed;

    public TunnelSet(SshClient client)
    {
        _client = client;
    }

    public event Action? Changed;

    public IReadOnlyList<ActiveTunnel> Snapshot()
    {
        lock (_gate)
        {
            return _tunnels.Values.ToList();
        }
    }

    public ActiveTunnel? Find(Guid id)
    {
        lock (_gate)
        {
            return _tunnels.GetValueOrDefault(id);
        }
    }

    public bool IsRunning(Guid id) => Find(id)?.State is TunnelState.Running or TunnelState.Starting;

    /// <summary>Abre o túnel. Erros ficam em <see cref="ActiveTunnel.Error"/> (não lança).</summary>
    public ActiveTunnel Start(TunnelDefinition definition)
    {
        ActiveTunnel tunnel;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TunnelSet));
            }

            if (_tunnels.TryGetValue(definition.Id, out var existing) && existing.State is TunnelState.Running or TunnelState.Starting)
            {
                return existing;
            }

            tunnel = new ActiveTunnel(definition.Copy()) { State = TunnelState.Starting };
            _tunnels[definition.Id] = tunnel;
        }

        Changed?.Invoke();
        if (definition.Validate() is { } invalid)
        {
            Fail(tunnel, invalid);
            return tunnel;
        }

        ForwardedPort port = definition.Kind switch
        {
            TunnelKind.Local => new ForwardedPortLocal(definition.BindHost, (uint)definition.BindPort, definition.TargetHost!, (uint)definition.TargetPort),
            TunnelKind.Remote => new ForwardedPortRemote(definition.BindHost, (uint)definition.BindPort, definition.TargetHost!, (uint)definition.TargetPort),
            _ => new ForwardedPortDynamic(definition.BindHost, (uint)definition.BindPort),
        };
        // Erros de conexões individuais (destino recusou) não derrubam o túnel: só ficam registrados.
        port.Exception += (_, e) =>
        {
            tunnel.Error = e.Exception.Message;
            Changed?.Invoke();
        };

        try
        {
            _client.AddForwardedPort(port);
            port.Start();
            tunnel.Port = port;
            tunnel.State = TunnelState.Running;
            tunnel.Error = null;
        }
        catch (Exception ex)
        {
            TryRemove(port);
            Fail(tunnel, Explain(definition, ex));
            return tunnel;
        }

        Changed?.Invoke();
        return tunnel;
    }

    public void Stop(Guid id)
    {
        ActiveTunnel? tunnel;
        lock (_gate)
        {
            if (!_tunnels.Remove(id, out tunnel))
            {
                return;
            }
        }

        if (tunnel.Port is { } port)
        {
            TryRemove(port);
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        List<ActiveTunnel> all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = _tunnels.Values.ToList();
            _tunnels.Clear();
        }

        foreach (var tunnel in all)
        {
            if (tunnel.Port is { } port)
            {
                TryRemove(port);
            }
        }
    }

    private void Fail(ActiveTunnel tunnel, string message)
    {
        tunnel.State = TunnelState.Failed;
        tunnel.Error = message;
        Changed?.Invoke();
    }

    private void TryRemove(ForwardedPort port)
    {
        try
        {
            if (port.IsStarted)
            {
                port.Stop();
            }
        }
        catch (Exception)
        {
            // A conexão pode já ter caído.
        }

        try
        {
            _client.RemoveForwardedPort(port);
        }
        catch (Exception)
        {
        }

        port.Dispose();
    }

    /// <summary>Mensagem curta para os erros mais comuns.</summary>
    internal static string Explain(TunnelDefinition definition, Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse } =>
            $"A porta {definition.BindPort} já está em uso neste computador.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied } =>
            $"Sem permissão para abrir a porta {definition.BindPort} neste computador.",
        Renci.SshNet.Common.SshException when definition.Kind == TunnelKind.Remote =>
            $"O servidor recusou abrir a porta {definition.BindPort} (pode estar em uso, ser menor que 1024 sem root, ou AllowTcpForwarding desligado).",
        _ => ex.Message,
    };
}
