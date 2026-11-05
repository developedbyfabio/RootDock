using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using RootDock.Core.Ssh;

namespace RootDock.Core.Tests;

/// <summary>Agente SSH falso (named pipe) que assina com Ed25519 de verdade.</summary>
public sealed class SshAgentTests : IDisposable
{
    private readonly string _pipe = "rootdock-test-agent-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly Ed25519PrivateKeyParameters _private;
    private readonly byte[] _edBlob;
    private readonly byte[] _rsaBlob = Blob("ssh-rsa", [1, 0, 1], [0x00, 0xC3, 0x11, 0x22]);
    private readonly List<uint> _flagsSeen = [];

    public SshAgentTests()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        _private = (Ed25519PrivateKeyParameters)pair.Private;
        _edBlob = Blob("ssh-ed25519", ((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
        _ = Task.Run(ServeAsync);
    }

    [Fact]
    public void ListKeys_ReadsTypesCommentsAndFingerprints()
    {
        var keys = SshAgentClient.ForPipe(_pipe, "teste").ListKeys();

        Assert.Equal(2, keys.Count);
        Assert.Equal("ssh-ed25519", keys[0].Type);
        Assert.Equal("eu@pc", keys[0].Comment);
        Assert.StartsWith("SHA256:", keys[0].Fingerprint);
        Assert.Equal("ssh-rsa", keys[1].Type);
    }

    [Fact]
    public void Ed25519_SignatureFromAgent_VerifiesWithPublicKey()
    {
        var agent = SshAgentClient.ForPipe(_pipe, "teste");
        var algorithm = Assert.Single(agent.KeySources().First().HostKeyAlgorithms);
        Assert.Equal("ssh-ed25519", algorithm.Name);
        Assert.Equal(_edBlob, algorithm.Data);

        var data = Encoding.UTF8.GetBytes("dados da autenticação");
        var signatureBlob = algorithm.Sign(data);

        var reader = new SshAgentClient.WireReader(signatureBlob, 0);
        Assert.Equal("ssh-ed25519", Encoding.ASCII.GetString(reader.Bytes()));
        var signature = reader.Bytes();
        var verifier = new Ed25519Signer();
        verifier.Init(false, _private.GeneratePublicKey());
        verifier.BlockUpdate(data, 0, data.Length);
        Assert.True(verifier.VerifySignature(signature));
    }

    [Fact]
    public void Rsa_OffersSha2First_AndSendsMatchingFlags()
    {
        var agent = SshAgentClient.ForPipe(_pipe, "teste");
        var rsa = agent.KeySources()[1].HostKeyAlgorithms.ToList();

        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], rsa.Select(a => a.Name));
        foreach (var algorithm in rsa)
        {
            algorithm.Sign([1, 2, 3]);
        }

        Assert.Equal([SshAgentClient.RsaSha512, SshAgentClient.RsaSha256, 0u], _flagsSeen);
    }

    // ------------------------------------------------------------------ agente falso

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var request = SshAgentClient.ReadFrame(server);
            SshAgentClient.WriteFrame(server, Answer(request));
        }
    }

    private byte[] Answer(byte[] request)
    {
        using var reply = new MemoryStream();
        if (request[0] == 11)
        {
            reply.WriteByte(12);
            SshAgentClient.WriteUInt32(reply, 2);
            SshAgentClient.WriteBytes(reply, _edBlob);
            SshAgentClient.WriteBytes(reply, Encoding.UTF8.GetBytes("eu@pc"));
            SshAgentClient.WriteBytes(reply, _rsaBlob);
            SshAgentClient.WriteBytes(reply, Encoding.UTF8.GetBytes("rsa"));
            return reply.ToArray();
        }

        var reader = new SshAgentClient.WireReader(request, 1);
        var blob = reader.Bytes();
        var data = reader.Bytes();
        var flags = reader.UInt32();
        reply.WriteByte(14);
        if (blob.SequenceEqual(_edBlob))
        {
            var signer = new Ed25519Signer();
            signer.Init(true, _private);
            signer.BlockUpdate(data, 0, data.Length);
            SshAgentClient.WriteBytes(reply, Blob("ssh-ed25519", signer.GenerateSignature()));
        }
        else
        {
            lock (_flagsSeen)
            {
                _flagsSeen.Add(flags);
            }

            SshAgentClient.WriteBytes(reply, Blob("rsa-sha2-256", [9, 9]));
        }

        return reply.ToArray();
    }

    private static byte[] Blob(string type, params byte[][] parts)
    {
        using var stream = new MemoryStream();
        SshAgentClient.WriteBytes(stream, Encoding.ASCII.GetBytes(type));
        foreach (var part in parts)
        {
            SshAgentClient.WriteBytes(stream, part);
        }

        return stream.ToArray();
    }

    public void Dispose() => _stop.Cancel();
}
