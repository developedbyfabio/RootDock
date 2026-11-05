using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace RootDock.Core.Network;

/// <summary>Uma rede IP calculada a partir de "endereço/prefixo" ou "endereço máscara".</summary>
public sealed record IpNetwork(
    IPAddress Address,
    int Prefix,
    IPAddress Network,
    IPAddress? Broadcast,
    IPAddress? Mask,
    IPAddress? Wildcard,
    IPAddress FirstHost,
    IPAddress LastHost,
    BigInteger TotalAddresses,
    BigInteger UsableHosts,
    string Kind)
{
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    public string Cidr => $"{Network}/{Prefix}";

    /// <summary>Endereços de host (IPv4), do primeiro ao último. Para varrer sub-redes.</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        if (IsIPv6)
        {
            yield break;
        }

        var first = IpCalculator.ToUInt32(FirstHost);
        var last = IpCalculator.ToUInt32(LastHost);
        for (var value = (ulong)first; value <= last; value++)
        {
            yield return IpCalculator.FromUInt32((uint)value);
        }
    }
}

/// <summary>Calculadora de sub-redes IPv4 e IPv6.</summary>
public static class IpCalculator
{
    /// <summary>
    /// Aceita "192.168.1.10/24", "192.168.1.10 255.255.255.0", "10.0.0.0/8", "2001:db8::1/64" ou
    /// só o endereço (vira /32 ou /128).
    /// </summary>
    public static IpNetwork Parse(string text)
    {
        var input = (text ?? "").Trim();
        if (input.Length == 0)
        {
            throw new FormatException("Informe um endereço, ex.: 192.168.0.10/24");
        }

        string addressPart;
        int? prefix = null;
        var slash = input.IndexOf('/');
        var space = input.IndexOfAny([' ', '\t']);
        if (slash >= 0)
        {
            addressPart = input[..slash].Trim();
            var p = input[(slash + 1)..].Trim();
            if (p.Contains('.'))
            {
                prefix = PrefixFromMask(p);
            }
            else if (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                prefix = n;
            }
            else
            {
                throw new FormatException($"Prefixo inválido: \"{p}\".");
            }
        }
        else if (space > 0)
        {
            addressPart = input[..space].Trim();
            prefix = PrefixFromMask(input[space..].Trim());
        }
        else
        {
            addressPart = input;
        }

        if (!IPAddress.TryParse(addressPart, out var address))
        {
            throw new FormatException($"Endereço IP inválido: \"{addressPart}\".");
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetwork
            ? CalculateV4(address, prefix ?? 32)
            : CalculateV6(address, prefix ?? 128);
    }

    private static IpNetwork CalculateV4(IPAddress address, int prefix)
    {
        if (prefix is < 0 or > 32)
        {
            throw new FormatException("O prefixo IPv4 vai de /0 a /32.");
        }

        var ip = ToUInt32(address);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ip & mask;
        var broadcast = network | ~mask;
        var total = (BigInteger)(1UL << (32 - prefix));
        uint first;
        uint last;
        BigInteger usable;
        if (prefix == 32)
        {
            first = last = network;
            usable = 1;
        }
        else if (prefix == 31)
        {
            first = network; // enlace ponto a ponto (RFC 3021): os dois endereços valem
            last = broadcast;
            usable = 2;
        }
        else
        {
            first = network + 1;
            last = broadcast - 1;
            usable = total - 2;
        }

        return new IpNetwork(address, prefix, FromUInt32(network), FromUInt32(broadcast), FromUInt32(mask), FromUInt32(~mask),
            FromUInt32(first), FromUInt32(last), total, usable, DescribeV4(network, prefix));
    }

    private static IpNetwork CalculateV6(IPAddress address, int prefix)
    {
        if (prefix is < 0 or > 128)
        {
            throw new FormatException("O prefixo IPv6 vai de /0 a /128.");
        }

        var bytes = address.GetAddressBytes();
        var network = (byte[])bytes.Clone();
        var last = (byte[])bytes.Clone();
        for (var bit = prefix; bit < 128; bit++)
        {
            var i = bit / 8;
            var m = (byte)(0x80 >> (bit % 8));
            network[i] &= (byte)~m;
            last[i] |= m;
        }

        var total = BigInteger.Pow(2, 128 - prefix);
        var kind = address.IsIPv6LinkLocal ? "IPv6 link-local (fe80::/10)"
            : address.IsIPv6UniqueLocal ? "IPv6 local único (fc00::/7, privado)"
            : IPAddress.IsLoopback(address) ? "Loopback"
            : bytes[0] == 0xff ? "Multicast"
            : (bytes[0] & 0xE0) == 0x20 ? "IPv6 global (público)"
            : "IPv6";
        return new IpNetwork(address, prefix, new IPAddress(network), null, null, null, new IPAddress(network), new IPAddress(last), total, total, kind);
    }

    private static string DescribeV4(uint network, int prefix)
    {
        static bool In(uint ip, uint net, int bits) => bits == 0 || (ip >> (32 - bits)) == (net >> (32 - bits));
        var parts = new List<string>();
        if (In(network, 0x0A000000, 8) || In(network, 0xAC100000, 12) || In(network, 0xC0A80000, 16))
        {
            parts.Add("Privado (RFC 1918)");
        }
        else if (In(network, 0x64400000, 10))
        {
            parts.Add("CGNAT (100.64.0.0/10)");
        }
        else if (In(network, 0x7F000000, 8))
        {
            parts.Add("Loopback");
        }
        else if (In(network, 0xA9FE0000, 16))
        {
            parts.Add("Link-local (APIPA)");
        }
        else if (In(network, 0xE0000000, 4))
        {
            parts.Add("Multicast");
        }
        else
        {
            parts.Add("Público");
        }

        var first = network >> 24;
        parts.Add(first < 128 ? "classe A" : first < 192 ? "classe B" : first < 224 ? "classe C" : first < 240 ? "classe D" : "classe E");
        if (prefix == 31)
        {
            parts.Add("ponto a ponto");
        }

        return string.Join(", ", parts);
    }

    private static int PrefixFromMask(string mask)
    {
        if (!IPAddress.TryParse(mask, out var m) || m.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FormatException($"Máscara inválida: \"{mask}\".");
        }

        var value = ToUInt32(m);
        var prefix = BitOperations.LeadingZeroCount(~value);
        if (prefix < 32 && (value << prefix) != 0)
        {
            throw new FormatException($"Máscara não contígua: \"{mask}\".");
        }

        return prefix;
    }

    internal static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    internal static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>Sub-redes de tamanho igual dentro da rede (ex.: dividir um /24 em quatro /26).</summary>
    public static IReadOnlyList<IpNetwork> Split(IpNetwork network, int newPrefix, int max = 256)
    {
        if (network.IsIPv6 || newPrefix <= network.Prefix || newPrefix > 32)
        {
            return [];
        }

        var list = new List<IpNetwork>();
        var start = ToUInt32(network.Network);
        var step = 1UL << (32 - newPrefix);
        var count = 1UL << (newPrefix - network.Prefix);
        for (ulong i = 0; i < count && list.Count < max; i++)
        {
            list.Add(CalculateV4(FromUInt32((uint)(start + (i * step))), newPrefix));
        }

        return list;
    }
}
