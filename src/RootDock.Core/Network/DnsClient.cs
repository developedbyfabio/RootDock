using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RootDock.Core.Network;

public enum DnsType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    PTR = 12,
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    CAA = 257,
    ANY = 255,
}

public sealed record DnsRecord(string Name, DnsType Type, int Ttl, string Data)
{
    public string TypeName => Enum.IsDefined(Type) ? Type.ToString() : $"TYPE{(ushort)Type}";
}

public sealed record DnsResponse(
    string Server,
    int ResponseCode,
    bool Authoritative,
    bool Truncated,
    IReadOnlyList<DnsRecord> Answers,
    IReadOnlyList<DnsRecord> Authority,
    TimeSpan Elapsed)
{
    /// <summary>Código de resposta em português.</summary>
    public string Status => ResponseCode switch
    {
        0 => Answers.Count > 0 ? "OK" : "Sem registros desse tipo",
        1 => "Consulta malformada (FORMERR)",
        2 => "Falha no servidor DNS (SERVFAIL)",
        3 => "Domínio não existe (NXDOMAIN)",
        4 => "Tipo de consulta não suportado (NOTIMP)",
        5 => "Consulta recusada pelo servidor (REFUSED)",
        _ => $"Código {ResponseCode}",
    };
}

/// <summary>
/// Cliente DNS próprio (UDP, com TCP quando a resposta vem truncada): consulta qualquer tipo de
/// registro em qualquer servidor, coisa que a API do Windows (só A/AAAA) não faz.
/// </summary>
public static class DnsClient
{
    /// <summary>DNS configurado no Windows (primeiro de uma interface ativa).</summary>
    public static IPAddress? SystemServer()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>Nome para consulta reversa: 4.3.2.1.in-addr.arpa ou nibbles .ip6.arpa.</summary>
    public static string ReverseName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Join('.', bytes.Reverse()) + ".in-addr.arpa";
        }

        var sb = new StringBuilder();
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            sb.Append((bytes[i] & 0xF).ToString("x")).Append('.').Append((bytes[i] >> 4).ToString("x")).Append('.');
        }

        return sb.Append("ip6.arpa").ToString();
    }

    public static async Task<DnsResponse> QueryAsync(string name, DnsType type, IPAddress server, TimeSpan timeout, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(id, name, type);
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            byte[] reply;
            using (var udp = new UdpClient(server.AddressFamily))
            {
                await udp.SendAsync(query, new IPEndPoint(server, 53), limit.Token).ConfigureAwait(false);
                while (true)
                {
                    var received = await udp.ReceiveAsync(limit.Token).ConfigureAwait(false);
                    if (received.Buffer.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(received.Buffer) == id)
                    {
                        reply = received.Buffer;
                        break;
                    }
                }
            }

            var response = Parse(reply, server.ToString(), watch.Elapsed);
            if (!response.Truncated)
            {
                return response;
            }

            // Resposta grande (muitos TXT, DNSSEC): repete por TCP.
            using var tcp = new TcpClient(server.AddressFamily);
            await tcp.ConnectAsync(server, 53, limit.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, limit.Token).ConfigureAwait(false);
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, limit.Token).ConfigureAwait(false);
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)];
            await stream.ReadExactlyAsync(body, limit.Token).ConfigureAwait(false);
            return Parse(body, server + " (TCP)", watch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"O servidor DNS {server} não respondeu em {timeout.TotalSeconds:0} s.");
        }
    }

    // ---------------------------------------------------------------- mensagem DNS (RFC 1035)
    public static byte[] BuildQuery(ushort id, string name, DnsType type)
    {
        var buffer = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // consulta padrão, recursão desejada
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // 1 pergunta
        buffer.AddRange(header.ToArray());
        foreach (var label in name.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var ascii = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(label));
            if (ascii.Length > 63)
            {
                throw new FormatException($"Parte do nome muito longa: \"{label}\".");
            }

            buffer.Add((byte)ascii.Length);
            buffer.AddRange(ascii);
        }

        buffer.Add(0);
        buffer.Add((byte)((ushort)type >> 8));
        buffer.Add((byte)type);
        buffer.Add(0);
        buffer.Add(1); // classe IN
        return [.. buffer];
    }

    public static DnsResponse Parse(byte[] data, string server, TimeSpan elapsed)
    {
        if (data.Length < 12)
        {
            throw new FormatException("Resposta DNS curta demais.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var questions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        var authority = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref offset);
            offset += 4;
        }

        var answerList = new List<DnsRecord>();
        for (var i = 0; i < answers && offset < data.Length; i++)
        {
            answerList.Add(ReadRecord(data, ref offset));
        }

        var authorityList = new List<DnsRecord>();
        for (var i = 0; i < authority && offset < data.Length; i++)
        {
            authorityList.Add(ReadRecord(data, ref offset));
        }

        return new DnsResponse(server, flags & 0xF, (flags & 0x0400) != 0, (flags & 0x0200) != 0, answerList, authorityList, elapsed);
    }

    private static DnsRecord ReadRecord(byte[] data, ref int offset)
    {
        var name = ReadName(data, ref offset);
        var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        var ttl = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4));
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
        offset += 10;
        var start = offset;
        if (start + length > data.Length)
        {
            throw new FormatException("Registro DNS cortado.");
        }

        offset += length;
        var rdata = data.AsSpan(start, length);
        var p = start;
        string text = type switch
        {
            DnsType.A when length == 4 => new IPAddress(rdata).ToString(),
            DnsType.AAAA when length == 16 => new IPAddress(rdata).ToString(),
            DnsType.NS or DnsType.CNAME or DnsType.PTR => ReadName(data, ref p),
            DnsType.MX => $"{BinaryPrimitives.ReadUInt16BigEndian(rdata)} {ReadNameAt(data, start + 2)}",
            DnsType.TXT => ReadCharacterStrings(rdata),
            DnsType.SOA => ReadSoa(data, start),
            DnsType.SRV => $"{BinaryPrimitives.ReadUInt16BigEndian(rdata)} {BinaryPrimitives.ReadUInt16BigEndian(rdata[2..])} {BinaryPrimitives.ReadUInt16BigEndian(rdata[4..])} {ReadNameAt(data, start + 6)}",
            DnsType.CAA when length >= 2 => $"{rdata[0]} {Encoding.ASCII.GetString(rdata.Slice(2, rdata[1]))} \"{Encoding.UTF8.GetString(rdata[(2 + rdata[1])..])}\"",
            _ => Convert.ToHexString(rdata),
        };
        return new DnsRecord(name, type, ttl, text);
    }

    private static string ReadSoa(byte[] data, int offset)
    {
        var p = offset;
        var mname = ReadName(data, ref p);
        var rname = ReadName(data, ref p);
        var s = data.AsSpan(p);
        return $"{mname} {rname} serial={BinaryPrimitives.ReadUInt32BigEndian(s)} refresh={BinaryPrimitives.ReadUInt32BigEndian(s[4..])} " +
               $"retry={BinaryPrimitives.ReadUInt32BigEndian(s[8..])} expire={BinaryPrimitives.ReadUInt32BigEndian(s[12..])} minimum={BinaryPrimitives.ReadUInt32BigEndian(s[16..])}";
    }

    private static string ReadCharacterStrings(ReadOnlySpan<byte> rdata)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < rdata.Length)
        {
            var len = rdata[i];
            var end = Math.Min(rdata.Length, i + 1 + len);
            parts.Add(Encoding.UTF8.GetString(rdata[(i + 1)..end]));
            i = end;
        }

        return string.Concat(parts.Select(p => "\"" + p + "\" ")).TrimEnd();
    }

    private static string ReadNameAt(byte[] data, int offset) => ReadName(data, ref offset);

    /// <summary>Nome com compressão (ponteiros para trechos anteriores da mensagem).</summary>
    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            if (position >= data.Length)
            {
                throw new FormatException("Nome DNS cortado.");
            }

            var len = data[position];
            if (len == 0)
            {
                position++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 32)
                {
                    throw new FormatException("Nome DNS com ponteiros em laço.");
                }

                var pointer = ((len & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = pointer;
                continue;
            }

            labels.Add(Encoding.ASCII.GetString(data, position + 1, len));
            position += len + 1;
        }

        if (!jumped)
        {
            offset = position;
        }

        return labels.Count == 0 ? "." : string.Join('.', labels);
    }
}
