using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace RootDock.Core.Ssh;

/// <summary>Chave carregada num agente SSH (a parte privada nunca sai do agente).</summary>
public sealed record AgentKey(byte[] Blob, string Type, string Comment, string Fingerprint, string Source);

/// <summary>
/// Cliente do protocolo do agente SSH (draft-miller-ssh-agent): lista as chaves e pede
/// assinaturas. Fala com o ssh-agent do Windows (named pipe) ou com o Pageant (WM_COPYDATA).
/// </summary>
public sealed class SshAgentClient
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>Flags do pedido de assinatura RSA (SHA-2).</summary>
    public const uint RsaSha256 = 2;
    public const uint RsaSha512 = 4;

    public const string OpenSshPipeName = "openssh-ssh-agent";

    private readonly Func<byte[], byte[]> _call;

    /// <param name="name">Nome para a tela ("ssh-agent do Windows", "Pageant").</param>
    /// <param name="call">Envia uma mensagem (sem o tamanho) e devolve a resposta (sem o tamanho).</param>
    public SshAgentClient(string name, Func<byte[], byte[]> call)
    {
        Name = name;
        _call = call;
    }

    public string Name { get; }

    /// <summary>Agente disponível nesta sessão do Windows: ssh-agent primeiro, depois Pageant.</summary>
    public static SshAgentClient? Find()
    {
        if (OperatingSystem.IsWindows() && File.Exists(@"\\.\pipe\" + OpenSshPipeName))
        {
            return ForPipe(OpenSshPipeName, "ssh-agent do Windows");
        }

        return Pageant.IsRunning ? new SshAgentClient("Pageant", Pageant.Call) : null;
    }

    public static SshAgentClient ForPipe(string pipeName, string displayName) =>
        new(displayName, payload =>
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(2000);
            WriteFrame(pipe, payload);
            return ReadFrame(pipe);
        });

    public IReadOnlyList<AgentKey> ListKeys()
    {
        var response = _call([RequestIdentities]);
        if (response.Length == 0 || response[0] != IdentitiesAnswer)
        {
            throw new InvalidOperationException($"{Name} recusou a listagem de chaves.");
        }

        var reader = new WireReader(response, 1);
        var count = reader.UInt32();
        var keys = new List<AgentKey>();
        for (var i = 0; i < count; i++)
        {
            var blob = reader.Bytes();
            var comment = Encoding.UTF8.GetString(reader.Bytes());
            var type = Encoding.ASCII.GetString(new WireReader(blob, 0).Bytes());
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
            keys.Add(new AgentKey(blob, type, comment, fingerprint, Name));
        }

        return keys;
    }

    /// <summary>Assina com a chave do agente. Devolve o blob de assinatura (algoritmo + assinatura).</summary>
    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        using var message = new MemoryStream();
        message.WriteByte(SignRequest);
        WriteBytes(message, keyBlob);
        WriteBytes(message, data);
        WriteUInt32(message, flags);
        var response = _call(message.ToArray());
        if (response.Length == 0 || response[0] != SignResponse)
        {
            throw new InvalidOperationException($"{Name} não assinou (a chave foi removida ou o agente recusou).");
        }

        return new WireReader(response, 1).Bytes();
    }

    /// <summary>Fontes de chave para a autenticação do SSH.NET (uma por chave do agente).</summary>
    public IReadOnlyList<IPrivateKeySource> KeySources() =>
        ListKeys().Select(k => new AgentKeySource(this, k)).Where(s => s.HostKeyAlgorithms.Count > 0).ToList<IPrivateKeySource>();

    // ------------------------------------------------------------------ formato de fio

    internal static void WriteFrame(Stream stream, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Flush();
    }

    internal static byte[] ReadFrame(Stream stream)
    {
        var header = new byte[4];
        stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 256 * 1024)
        {
            throw new InvalidDataException("Resposta do agente grande demais.");
        }

        var body = new byte[length];
        stream.ReadExactly(body);
        return body;
    }

    internal static void WriteBytes(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    internal static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    internal sealed class WireReader(byte[] data, int offset)
    {
        private int _offset = offset;

        public uint UInt32()
        {
            if (_offset + 4 > data.Length)
            {
                throw new InvalidDataException("Mensagem do agente incompleta.");
            }

            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public byte[] Bytes()
        {
            var length = (int)UInt32();
            if (length < 0 || _offset + length > data.Length)
            {
                throw new InvalidDataException("Mensagem do agente incompleta.");
            }

            var value = data.AsSpan(_offset, length).ToArray();
            _offset += length;
            return value;
        }
    }

    // ------------------------------------------------------------------ Pageant

    /// <summary>Pageant (PuTTY): pedido por memória compartilhada + WM_COPYDATA.</summary>
    private static class Pageant
    {
        private const int WmCopyData = 0x004A;
        private const uint AgentCopyDataId = 0x804e50ba;
        private const int MaxMessage = 8192;

        public static bool IsRunning => OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != IntPtr.Zero;

        public static byte[] Call(byte[] payload)
        {
            var window = FindWindow("Pageant", "Pageant");
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("O Pageant não está aberto.");
            }

            if (payload.Length + 4 > MaxMessage)
            {
                throw new InvalidOperationException("Pedido grande demais para o Pageant.");
            }

            var mapName = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Random.Shared.Next():x8}";
            using var map = MemoryMappedFile.CreateNew(mapName, MaxMessage);
            using var view = map.CreateViewStream();
            WriteFrame(view, payload);

            var name = Encoding.ASCII.GetBytes(mapName + "\0");
            var handle = GCHandle.Alloc(name, GCHandleType.Pinned);
            try
            {
                var data = new CopyDataStruct { Data = AgentCopyDataId, Size = name.Length, Pointer = handle.AddrOfPinnedObject() };
                if (SendMessage(window, WmCopyData, IntPtr.Zero, ref data) == IntPtr.Zero)
                {
                    throw new InvalidOperationException("O Pageant recusou o pedido.");
                }
            }
            finally
            {
                handle.Free();
            }

            view.Position = 0;
            return ReadFrame(view);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyDataStruct
        {
            public nuint Data; // ULONG_PTR: mesmo valor em 32 e 64 bits
            public int Size;
            public IntPtr Pointer;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref CopyDataStruct lParam);
    }
}

/// <summary>Chave do agente vista pelo SSH.NET como uma "chave privada" que assina pelo agente.</summary>
internal sealed class AgentKeySource : IPrivateKeySource
{
    public AgentKeySource(SshAgentClient agent, AgentKey key)
    {
        HostKeyAlgorithms = key.Type switch
        {
            // RSA: as variantes SHA-2 primeiro; "ssh-rsa" (SHA-1) só para servidores antigos.
            "ssh-rsa" =>
            [
                new AgentHostAlgorithm("rsa-sha2-512", agent, key.Blob, SshAgentClient.RsaSha512),
                new AgentHostAlgorithm("rsa-sha2-256", agent, key.Blob, SshAgentClient.RsaSha256),
                new AgentHostAlgorithm("ssh-rsa", agent, key.Blob, 0),
            ],
            "ssh-ed25519" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" =>
                [new AgentHostAlgorithm(key.Type, agent, key.Blob, 0)],
            _ => [], // chaves de segurança (sk-*) não são suportadas pelo SSH.NET
        };
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }
}

internal sealed class AgentHostAlgorithm(string name, SshAgentClient agent, byte[] blob, uint flags) : HostAlgorithm(name)
{
    public override byte[] Data => blob;

    public override byte[] Sign(byte[] data) => agent.Sign(blob, data, flags);

    public override bool VerifySignature(byte[] data, byte[] signature) =>
        throw new NotSupportedException("Chaves do agente só assinam.");
}
