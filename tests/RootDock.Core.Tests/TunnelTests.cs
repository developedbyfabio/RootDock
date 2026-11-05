using System.Net.Sockets;
using RootDock.Core.Sessions;
using RootDock.Core.Ssh;

namespace RootDock.Core.Tests;

public sealed class TunnelTests
{
    [Theory]
    [InlineData(TunnelKind.Local, "127.0.0.1", 3307, "127.0.0.1", 3306, null)]
    [InlineData(TunnelKind.Dynamic, "127.0.0.1", 1080, null, 0, null)]
    [InlineData(TunnelKind.Local, "127.0.0.1", 0, "db", 3306, "porta de escuta")]
    [InlineData(TunnelKind.Remote, "127.0.0.1", 8080, "", 3000, "destino")]
    [InlineData(TunnelKind.Local, "127.0.0.1", 3307, "db", 70000, "porta de destino")]
    [InlineData(TunnelKind.Local, " ", 3307, "db", 3306, "escuta")]
    public void Validate_ExplainsWhatIsMissing(TunnelKind kind, string bind, int bindPort, string? target, int targetPort, string? error)
    {
        var tunnel = new TunnelDefinition { Kind = kind, BindHost = bind, BindPort = bindPort, TargetHost = target, TargetPort = targetPort };
        var message = tunnel.Validate();
        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(error, message);
        }
    }

    [Fact]
    public void Describe_ReadsLikeTheSshFlags()
    {
        Assert.Equal("127.0.0.1:3307  →  10.0.0.5:3306 (pelo servidor)",
            new TunnelDefinition { Kind = TunnelKind.Local, BindPort = 3307, TargetHost = "10.0.0.5", TargetPort = 3306 }.Describe());
        Assert.StartsWith("SOCKS 127.0.0.1:1080",
            new TunnelDefinition { Kind = TunnelKind.Dynamic, BindPort = 1080 }.Describe());
        Assert.True(new TunnelDefinition { BindHost = "0.0.0.0" }.IsExposed);
    }

    [Fact]
    public void Explain_PortInUse_IsFriendly()
    {
        var tunnel = new TunnelDefinition { Kind = TunnelKind.Local, BindPort = 3307 };
        var message = TunnelSet.Explain(tunnel, new SocketException((int)SocketError.AddressAlreadyInUse));
        Assert.Contains("3307 já está em uso", message);
    }

    [Fact]
    public void SessionClone_CopiesTunnelsWithNewIds_CopyKeepsIds()
    {
        var entry = new SessionEntry();
        entry.Tunnels.Add(new TunnelDefinition { Kind = TunnelKind.Local, BindPort = 1, TargetHost = "x", TargetPort = 2 });

        var copy = entry.Copy();
        var clone = entry.Clone();
        copy.Tunnels[0].BindPort = 99;

        Assert.Equal(1, entry.Tunnels[0].BindPort); // lista e itens independentes
        Assert.Equal(entry.Tunnels[0].Id, copy.Tunnels[0].Id);
        Assert.NotEqual(entry.Tunnels[0].Id, clone.Tunnels[0].Id);
    }

    [Fact]
    public void Route_WithoutJumps_IsTheServerItself()
    {
        using var route = SshRoute.Direct("srv", 2222);
        Assert.Equal(("srv", 2222), (route.ConnectHost, route.ConnectPort));
        Assert.InRange(SshRoute.FreeLoopbackPort(), 1, 65535);
    }
}
