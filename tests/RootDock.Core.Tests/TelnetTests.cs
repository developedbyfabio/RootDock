using System.Net;
using System.Net.Sockets;
using System.Text;
using RootDock.Core.Serial;
using RootDock.Core.Sessions;
using RootDock.Core.Telnet;
using static RootDock.Core.Telnet.TelnetConnection;

namespace RootDock.Core.Tests;

public sealed class TelnetTests
{
    [Fact]
    public void Process_StripsCommands_KeepsEscapedIac_AndCrNul()
    {
        var telnet = new TelnetConnection("x", 23);
        byte[] input = [(byte)'a', Iac, Iac, (byte)'b', Iac, 241 /* NOP */, (byte)'\r', 0, (byte)'c'];
        Assert.Equal([(byte)'a', 255, (byte)'b', (byte)'\r', (byte)'c'], telnet.Process(input));
    }

    [Fact]
    public void Process_HandlesCommandsSplitAcrossPackets()
    {
        var telnet = new TelnetConnection("x", 23);
        var first = telnet.Process([(byte)'o', (byte)'k', Iac]);
        var second = telnet.Process([Iac, (byte)'!']);
        Assert.Equal("ok", Encoding.ASCII.GetString(first));
        Assert.Equal([255, (byte)'!'], second);
    }

    [Fact]
    public void WindowSize_EscapesIacInValues()
    {
        // 255 colunas: o byte 0xFF precisa ir duplicado.
        Assert.Equal([Iac, Sb, OptNaws, 0, 255, 255, 0, 24, Iac, Se], WindowSize(255, 24));
    }

    /// <summary>Servidor Telnet falso: negocia, pede login e confere o que o cliente respondeu.</summary>
    [Fact]
    public async Task Negotiates_SendsSizeAndType_AndLogsInAutomatically()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fromClient = new MemoryStream();
        var serverDone = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            await stream.WriteAsync(new byte[] { Iac, Do, OptNaws, Iac, Do, OptTtype, Iac, Sb, OptTtype, 1, Iac, Se, Iac, Will, OptEcho, Iac, Do, 39 });
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\nSwitch\r\nUsername: "));
            await ReadUntilAsync(stream, fromClient, "admin\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("Password: "));
            await ReadUntilAsync(stream, fromClient, "s3nha\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\nSwitch# "));
        });

        using var telnet = new TelnetConnection("127.0.0.1", port, new TelnetAutoLogin("admin", "s3nha"));
        var screen = new StringBuilder();
        var prompt = new TaskCompletionSource();
        telnet.DataReceived += data =>
        {
            lock (screen)
            {
                screen.Append(Encoding.ASCII.GetString(data.Span));
                if (screen.ToString().Contains("Switch# "))
                {
                    prompt.TrySetResult();
                }
            }
        };
        await telnet.ConnectAsync(120, 40, CancellationToken.None);
        telnet.Start();

        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await serverDone.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        var sent = fromClient.ToArray();
        Assert.True(Contains(sent, WindowSize(120, 40)), "tamanho da janela (NAWS)");
        Assert.True(Contains(sent, [Iac, Sb, OptTtype, 0, .. Encoding.ASCII.GetBytes("XTERM-256COLOR"), Iac, Se]), "tipo de terminal");
        Assert.True(Contains(sent, [Iac, Wont, 39]), "opção desconhecida recusada");
        Assert.DoesNotContain(Iac, Encoding.Latin1.GetBytes(screen.ToString())); // nada de comando na tela
        Assert.Contains("Username: ", screen.ToString());
    }

    [Fact]
    public void Serial_DescribeAndValidate()
    {
        var settings = new SerialSettings { PortName = "COM3", BaudRate = 115200 };
        Assert.Equal("COM3 · 115200 8N1", settings.Describe());
        Assert.Null(settings.Validate());
        Assert.NotNull((settings with { DataBits = 9 }).Validate());
        Assert.NotNull((settings with { PortName = "" }).Validate());
        Assert.Equal("COM3 · 9600 7E1 XON/XOFF", (settings with { BaudRate = 9600, DataBits = 7, Parity = "Even", FlowControl = SerialFlowControl.XonXoff }).Describe());
    }

    [Fact]
    public void Endpoint_ShowsProtocolSpecificTarget()
    {
        Assert.Equal("telnet://admin@sw01", new SessionEntry { Kind = SessionKind.Telnet, Host = "sw01", Port = 23, Username = "admin" }.Endpoint);
        Assert.Equal("telnet://sw01:2323", new SessionEntry { Kind = SessionKind.Telnet, Host = "sw01", Port = 2323 }.Endpoint);
        Assert.Equal("COM4 · 9600 8N1", new SessionEntry { Kind = SessionKind.Serial, Serial = new SerialSettings { PortName = "COM4" } }.Endpoint);
    }

    private static async Task ReadUntilAsync(NetworkStream stream, MemoryStream capture, string text)
    {
        var buffer = new byte[1024];
        var expected = Encoding.ASCII.GetBytes(text);
        while (!Contains(capture.ToArray(), expected))
        {
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (read == 0)
            {
                return;
            }

            capture.Write(buffer, 0, read);
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
