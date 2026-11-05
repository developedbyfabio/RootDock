using System.Net;
using RootDock.Core.Network;

namespace RootDock.Core.Tests;

public sealed class NetworkToolsTests
{
    [Fact]
    public void Ipv4_Slash24()
    {
        var n = IpCalculator.Parse("192.168.10.77/24");
        Assert.Equal("192.168.10.0", n.Network.ToString());
        Assert.Equal("192.168.10.255", n.Broadcast!.ToString());
        Assert.Equal("255.255.255.0", n.Mask!.ToString());
        Assert.Equal("0.0.0.255", n.Wildcard!.ToString());
        Assert.Equal("192.168.10.1", n.FirstHost.ToString());
        Assert.Equal("192.168.10.254", n.LastHost.ToString());
        Assert.Equal(254, (int)n.UsableHosts);
        Assert.Contains("Privado", n.Kind);
        Assert.Equal("192.168.10.0/24", n.Cidr);
    }

    [Theory]
    [InlineData("10.1.2.3 255.255.252.0", 22, "10.1.0.0", "10.1.3.255", 1022)]
    [InlineData("172.16.5.4/12", 12, "172.16.0.0", "172.31.255.255", 1048574)]
    [InlineData("8.8.8.8", 32, "8.8.8.8", "8.8.8.8", 1)]
    [InlineData("100.64.1.1/31", 31, "100.64.1.0", "100.64.1.1", 2)]
    public void Ipv4_Variants(string input, int prefix, string network, string broadcast, long usable)
    {
        var n = IpCalculator.Parse(input);
        Assert.Equal(prefix, n.Prefix);
        Assert.Equal(network, n.Network.ToString());
        Assert.Equal(broadcast, n.Broadcast!.ToString());
        Assert.Equal(usable, (long)n.UsableHosts);
    }

    [Theory]
    [InlineData("300.1.1.1/24")]
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1 255.0.255.0")]
    [InlineData("")]
    public void Invalid_InputsExplain(string input) => Assert.Throws<FormatException>(() => IpCalculator.Parse(input));

    [Fact]
    public void Ipv6_Prefix()
    {
        var n = IpCalculator.Parse("2001:db8:abcd:12::1/64");
        Assert.Equal("2001:db8:abcd:12::", n.Network.ToString());
        Assert.Equal("2001:db8:abcd:12:ffff:ffff:ffff:ffff", n.LastHost.ToString());
        Assert.Equal(System.Numerics.BigInteger.Pow(2, 64), n.TotalAddresses);
    }

    [Fact]
    public void Split_And_Hosts()
    {
        var n = IpCalculator.Parse("192.168.0.0/24");
        var parts = IpCalculator.Split(n, 26);
        Assert.Equal(["192.168.0.0/26", "192.168.0.64/26", "192.168.0.128/26", "192.168.0.192/26"], parts.Select(p => p.Cidr));
        Assert.Equal(254, IpCalculator.Parse("10.0.0.0/24").Hosts().Count());
    }

    [Fact]
    public void Dns_QueryHasHeaderAndQuestion()
    {
        var q = DnsClient.BuildQuery(0xBEEF, "www.exemplo.com.br", DnsType.MX);
        Assert.Equal(0xBE, q[0]);
        Assert.Equal(0xEF, q[1]);
        Assert.Equal(0x01, q[2]); // recursão desejada
        Assert.Equal(1, q[5]);    // 1 pergunta
        Assert.Equal(3, q[12]);   // "www"
        Assert.Equal((byte)'w', q[13]);
        Assert.Equal(15, q[^3]);  // tipo MX
        Assert.Equal(1, q[^1]);   // classe IN
    }

    [Fact]
    public void Dns_ParsesAnswersWithCompression()
    {
        // Resposta para "ex.com" com: A 93.184.216.34 e MX 10 mail.ex.com (nome comprimido).
        var bytes = new List<byte>
        {
            0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
            2, (byte)'e', (byte)'x', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0x00, 0x01, 0x00, 0x01,
            // A: nome = ponteiro para 12
            0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x0E, 0x10, 0x00, 0x04, 93, 184, 216, 34,
            // MX: preferência 10, "mail" + ponteiro para "ex.com"
            0xC0, 0x0C, 0x00, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x0E, 0x10, 0x00, 0x09, 0x00, 0x0A, 4, (byte)'m', (byte)'a', (byte)'i', (byte)'l', 0xC0, 0x0C,
        };

        var response = DnsClient.Parse([.. bytes], "teste", TimeSpan.Zero);

        Assert.Equal(0, response.ResponseCode);
        Assert.Equal(2, response.Answers.Count);
        Assert.Equal("ex.com", response.Answers[0].Name);
        Assert.Equal("93.184.216.34", response.Answers[0].Data);
        Assert.Equal(3600, response.Answers[0].Ttl);
        Assert.Equal(DnsType.MX, response.Answers[1].Type);
        Assert.Equal("10 mail.ex.com", response.Answers[1].Data);
        Assert.Equal("OK", response.Status);
    }

    [Fact]
    public void Dns_NxDomainIsExplained()
    {
        byte[] bytes = [0, 1, 0x81, 0x83, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Contains("NXDOMAIN", DnsClient.Parse(bytes, "x", TimeSpan.Zero).Status);
    }

    [Fact]
    public void Dns_ReverseNames()
    {
        Assert.Equal("4.3.2.1.in-addr.arpa", DnsClient.ReverseName(IPAddress.Parse("1.2.3.4")));
        Assert.StartsWith("1.0.0.0.", DnsClient.ReverseName(IPAddress.Parse("2001:db8::1")));
        Assert.EndsWith(".8.b.d.0.1.0.0.2.ip6.arpa", DnsClient.ReverseName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void Ports_ParseListsAndRanges()
    {
        Assert.Equal([22, 80, 443, 8000, 8001, 8002], PortList.Parse("22, 443,80 8000-8002"));
        Assert.Contains(3306, PortList.Parse("comuns"));
        Assert.Equal("MySQL", PortList.ServiceName(3306));
        Assert.Throws<FormatException>(() => PortList.Parse("70000"));
    }

    [Theory]
    [InlineData("refer:        whois.verisign-grs.com\n", "whois.verisign-grs.com")]
    [InlineData("   Registrar WHOIS Server: whois.registrar.com\r\n", "whois.registrar.com")]
    [InlineData("ReferralServer:  whois://whois.lacnic.net\n", "whois.lacnic.net")]
    [InlineData("domain: exemplo.com.br\nowner: Alguém\n", null)]
    public void Whois_FollowsReferrals(string text, string? expected) => Assert.Equal(expected, Whois.Referral(text));
}
