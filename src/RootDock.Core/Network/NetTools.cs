using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RootDock.Core.Network;

// ==================================================================== Portas
public sealed record PortResult(int Port, string State, string Service, TimeSpan? Elapsed);

/// <summary>Lista de portas: "22,80,443,8000-8100" ou "comuns".</summary>
public static class PortList
{
    public static readonly IReadOnlyDictionary<int, string> Known = new Dictionary<int, string>
    {
        [20] = "FTP dados", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [110] = "POP3",
        [111] = "rpcbind", [135] = "RPC Windows", [139] = "NetBIOS", [143] = "IMAP", [161] = "SNMP", [389] = "LDAP", [443] = "HTTPS",
        [445] = "SMB", [465] = "SMTPS", [587] = "SMTP envio", [631] = "IPP", [636] = "LDAPS", [873] = "rsync", [993] = "IMAPS",
        [995] = "POP3S", [1433] = "SQL Server", [1521] = "Oracle", [2049] = "NFS", [2375] = "Docker", [2376] = "Docker TLS",
        [3000] = "Node/Grafana", [3306] = "MySQL", [3389] = "RDP", [5000] = "App", [5432] = "PostgreSQL", [5672] = "RabbitMQ",
        [5900] = "VNC", [5985] = "WinRM", [5986] = "WinRM HTTPS", [6379] = "Redis", [6443] = "Kubernetes API", [8000] = "HTTP alt",
        [8080] = "HTTP proxy/alt", [8081] = "HTTP alt", [8443] = "HTTPS alt", [9000] = "PHP-FPM/Portainer", [9090] = "Prometheus/Cockpit",
        [9200] = "Elasticsearch", [10050] = "Zabbix agente", [10051] = "Zabbix servidor", [11211] = "Memcached", [15672] = "RabbitMQ painel",
        [27017] = "MongoDB",
    };

    public static IReadOnlyList<int> Common => [.. Known.Keys.Order()];

    public static IReadOnlyList<int> Parse(string text)
    {
        var input = (text ?? "").Trim();
        if (input.Length == 0 || input.Equals("comuns", StringComparison.OrdinalIgnoreCase))
        {
            return Common;
        }

        var ports = new SortedSet<int>();
        foreach (var part in input.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                var from = ParsePort(part[..dash]);
                var to = ParsePort(part[(dash + 1)..]);
                if (to < from)
                {
                    (from, to) = (to, from);
                }

                if (to - from > 65535)
                {
                    throw new FormatException("Faixa grande demais.");
                }

                for (var p = from; p <= to; p++)
                {
                    ports.Add(p);
                }
            }
            else
            {
                ports.Add(ParsePort(part));
            }
        }

        return [.. ports];
    }

    private static int ParsePort(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new FormatException($"Porta inválida: \"{text.Trim()}\" (use 1 a 65535).");

    public static string ServiceName(int port) => Known.TryGetValue(port, out var name) ? name : "";
}

public static class PortScanner
{
    /// <summary>Testa se as portas TCP aceitam conexão. Use só em redes e servidores que você administra.</summary>
    public static async Task ScanAsync(
        string host, IReadOnlyList<int> ports, TimeSpan timeout, int concurrency, Action<PortResult> report, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Não foi possível resolver {host}.");
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 256));
        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                report(await ProbeAsync(address, port, timeout, ct).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static async Task<PortResult> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(address, port, limit.Token).ConfigureAwait(false);
            return new PortResult(port, "aberta", PortList.ServiceName(port), watch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PortResult(port, "sem resposta (filtrada)", PortList.ServiceName(port), null);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new PortResult(port, "fechada", PortList.ServiceName(port), watch.Elapsed);
        }
        catch (SocketException ex)
        {
            return new PortResult(port, $"erro ({ex.SocketErrorCode})", PortList.ServiceName(port), null);
        }
    }
}

// ==================================================================== Ping, traceroute, MTR, varredura
public sealed record HopResult(int Ttl, IPAddress? Address, string? HostName, IReadOnlyList<long?> Times, bool Reached);

public static class Tracer
{
    /// <summary>Traceroute por ICMP (o mesmo do tracert do Windows): 3 sondas por salto.</summary>
    public static async Task TraceAsync(string host, int maxHops, TimeSpan timeout, bool resolveNames, Action<HopResult> report, CancellationToken ct)
    {
        var target = await ResolveAsync(host, ct).ConfigureAwait(false);
        var buffer = new byte[32];
        using var ping = new Ping();
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            IPAddress? hop = null;
            var reached = false;
            var times = new List<long?>();
            for (var probe = 0; probe < 3; probe++)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var reply = await ping.SendPingAsync(target, timeout, buffer, new PingOptions(ttl, true), ct).ConfigureAwait(false);
                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        hop ??= reply.Address;
                        times.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : watch.ElapsedMilliseconds);
                        reached |= reply.Status == IPStatus.Success;
                    }
                    else
                    {
                        times.Add(null);
                    }
                }
                catch (PingException)
                {
                    times.Add(null);
                }
            }

            var name = resolveNames && hop is not null ? await ReverseAsync(hop, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false) : null;
            report(new HopResult(ttl, hop, name, times, reached));
            if (reached)
            {
                return;
            }
        }
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim(), out var ip))
        {
            return ip;
        }

        var addresses = await Dns.GetHostAddressesAsync(host.Trim(), ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Não foi possível resolver {host}.");
    }

    public static async Task<string?> ReverseAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        var lookup = Dns.GetHostEntryAsync(address.ToString(), ct);
        try
        {
            var entry = await lookup.WaitAsync(timeout, ct).ConfigureAwait(false);
            return entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
        }
        finally
        {
            // Desistiu antes (prazo ou cancelado): a consulta segue e pode falhar depois ("host não
            // conhecido" = endereço sem nome reverso). O erro é esperado; só não pode ficar sem dono.
            if (!lookup.IsCompleted)
            {
                _ = lookup.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }
}

/// <summary>Estatística de um salto no MTR (traceroute contínuo).</summary>
public sealed class MtrHop
{
    public int Ttl { get; init; }

    public IPAddress? Address { get; set; }

    public string? HostName { get; set; }

    public int Sent { get; set; }

    public int Received { get; set; }

    public long Last { get; set; }

    public long Best { get; set; } = long.MaxValue;

    public long Worst { get; set; }

    public long Total { get; set; }

    public double Loss => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    public double Average => Received == 0 ? 0 : (double)Total / Received;
}

public static class Mtr
{
    /// <summary>Roda o traceroute sem parar, acumulando perda e tempos por salto (como o mtr do Linux).</summary>
    public static async Task RunAsync(string host, int maxHops, TimeSpan interval, Action<IReadOnlyList<MtrHop>> report, CancellationToken ct)
    {
        var target = await Tracer.ResolveAsync(host, ct).ConfigureAwait(false);
        var hops = new List<MtrHop>();
        var buffer = new byte[32];
        var lastTtl = maxHops;
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            for (var ttl = 1; ttl <= lastTtl && !ct.IsCancellationRequested; ttl++)
            {
                while (hops.Count < ttl)
                {
                    hops.Add(new MtrHop { Ttl = hops.Count + 1 });
                }

                var hop = hops[ttl - 1];
                hop.Sent++;
                try
                {
                    var watch = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(target, TimeSpan.FromSeconds(1), buffer, new PingOptions(ttl, true), ct).ConfigureAwait(false);
                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        var rtt = reply.Status == IPStatus.Success ? reply.RoundtripTime : watch.ElapsedMilliseconds;
                        hop.Received++;
                        hop.Last = rtt;
                        hop.Total += rtt;
                        hop.Best = Math.Min(hop.Best, rtt);
                        hop.Worst = Math.Max(hop.Worst, rtt);
                        if (hop.Address is null || !hop.Address.Equals(reply.Address))
                        {
                            hop.Address = reply.Address;
                            hop.HostName = null;
                            var h = hop;
                            var address = reply.Address;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    h.HostName = await Tracer.ReverseAsync(address, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // MTR parado
                                }
                            }, CancellationToken.None);
                        }

                        if (reply.Status == IPStatus.Success)
                        {
                            lastTtl = ttl;
                            if (hops.Count > ttl)
                            {
                                hops.RemoveRange(ttl, hops.Count - ttl);
                            }
                        }
                    }
                }
                catch (PingException)
                {
                    // sem resposta: conta como perda
                }
            }

            report(hops.ToList());
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public sealed record SweepResult(IPAddress Address, long RoundtripMs, string? HostName, string? Mac);

public static class SubnetScanner
{
    public const int MaxHosts = 4096;

    /// <summary>Ping em todos os hosts da rede (até /20). Mostra quem respondeu, com nome e MAC (rede local).</summary>
    public static async Task ScanAsync(IpNetwork network, TimeSpan timeout, int concurrency, bool resolveNames, Action<SweepResult> report, Action<int, int> progress, CancellationToken ct)
    {
        if (network.IsIPv6)
        {
            throw new InvalidOperationException("A varredura funciona só com IPv4.");
        }

        var hosts = network.Hosts().Take(MaxHosts + 1).ToList();
        if (hosts.Count > MaxHosts)
        {
            throw new InvalidOperationException($"Rede grande demais para varrer (máximo {MaxHosts} hosts, ex.: /20).");
        }

        var done = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 256));
        var alive = new List<(IPAddress Address, long Rtt)>();
        var tasks = hosts.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var ping = new Ping();
                try
                {
                    var reply = await ping.SendPingAsync(address, timeout, new byte[16], null, ct).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        lock (alive)
                        {
                            alive.Add((address, reply.RoundtripTime));
                        }

                        var name = resolveNames ? await Tracer.ReverseAsync(address, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false) : null;
                        report(new SweepResult(address, reply.RoundtripTime, name, ArpTable.MacOf(address)));
                    }
                }
                catch (PingException)
                {
                }
            }
            finally
            {
                gate.Release();
                progress(Interlocked.Increment(ref done), hosts.Count);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

// ==================================================================== Tabela ARP
public sealed record ArpEntry(IPAddress Address, string Mac, string Type, int InterfaceIndex);

public static class ArpTable
{
    public static IReadOnlyList<ArpEntry> Read()
    {
        var size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, true);
        if (size <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, true) != 0)
            {
                return [];
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpNetRow>();
            var list = new List<ArpEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(buffer + 4 + (i * rowSize));
                if (row.dwPhysAddrLen == 0 || row.dwType == 2)
                {
                    continue; // inválida
                }

                var mac = string.Join('-', row.bPhysAddr.Take(row.dwPhysAddrLen).Select(b => b.ToString("X2")));
                var type = row.dwType switch { 3 => "dinâmico", 4 => "estático", _ => "outro" };
                list.Add(new ArpEntry(new IPAddress(BitConverter.GetBytes(row.dwAddr)), mac, type, row.dwIndex));
            }

            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? MacOf(IPAddress address)
    {
        try
        {
            return Read().FirstOrDefault(e => e.Address.Equals(address))?.Mac;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public uint dwAddr;
        public int dwType;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);
}

// ==================================================================== WHOIS
public static class Whois
{
    /// <summary>Consulta a IANA e segue o servidor indicado (registro do domínio/IP e, se houver, o registrador).</summary>
    public static async Task<IReadOnlyList<(string Server, string Text)>> LookupAsync(string query, CancellationToken ct)
    {
        query = query.Trim().TrimEnd('.');
        var results = new List<(string, string)>();
        var server = "whois.iana.org";
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var step = 0; step < 3 && server.Length > 0 && visited.Add(server); step++)
        {
            var text = await QueryAsync(server, query, ct).ConfigureAwait(false);
            results.Add((server, text));
            server = Referral(text) ?? "";
        }

        return results;
    }

    /// <summary>Servidor indicado na resposta ("refer:", "whois:", "Registrar WHOIS Server:").</summary>
    public static string? Referral(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var key in new[] { "refer:", "whois:", "Registrar WHOIS Server:", "ReferralServer:" })
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[key.Length..].Trim();
                    if (value.StartsWith("whois://", StringComparison.OrdinalIgnoreCase))
                    {
                        value = value["whois://".Length..];
                    }

                    value = value.Split(':')[0].Trim();
                    if (value.Length > 0 && value.Contains('.') && !value.Contains(' '))
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    private static async Task<string> QueryAsync(string server, string query, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server, 43, limit.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        // ARIN espera "n + ip" para resumo; os demais aceitam a consulta pura.
        var request = server.Equals("whois.arin.net", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(query, out _) ? $"n + {query}" : query;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request + "\r\n"), limit.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, limit.Token).ConfigureAwait(false);
        var bytes = memory.ToArray();
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes); // registro.br e outros antigos
        }
    }
}

// ==================================================================== Certificado TLS
public sealed record CertificateInfo(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Thumbprint, IReadOnlyList<string> Names, string KeyAlgorithm)
{
    public int DaysLeft => (int)Math.Floor((NotAfter - DateTime.Now).TotalDays);
}

public sealed record TlsReport(
    string Host,
    int Port,
    string Protocol,
    string Cipher,
    TimeSpan Elapsed,
    CertificateInfo Certificate,
    IReadOnlyList<CertificateInfo> Chain,
    IReadOnlyList<string> Problems);

public static class TlsInspector
{
    public static async Task<TlsReport> InspectAsync(string host, int port, CancellationToken ct)
    {
        var problems = new List<string>();
        X509Certificate2? leaf = null;
        var chainInfos = new List<CertificateInfo>();
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, chain, errors) =>
        {
            if (certificate is not null)
            {
                leaf = new X509Certificate2(certificate);
            }

            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    chainInfos.Add(Describe(element.Certificate));
                }

                foreach (var status in chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError))
                {
                    problems.Add(TranslateChainStatus(status));
                }
            }

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                problems.Add($"O certificado não é para \"{host}\" (nome diferente).");
            }

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                problems.Add("O servidor não enviou certificado.");
            }

            return true; // só inspeciona; nenhum dado é enviado
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, limit.Token).ConfigureAwait(false);
        var elapsed = watch.Elapsed;
        var info = leaf is null ? throw new InvalidOperationException("Nenhum certificado recebido.") : Describe(leaf);
        if (info.DaysLeft < 0)
        {
            problems.Insert(0, $"Certificado VENCIDO há {-info.DaysLeft} dia(s).");
        }
        else if (info.DaysLeft <= 21)
        {
            problems.Insert(0, $"Certificado vence em {info.DaysLeft} dia(s).");
        }

#pragma warning disable SYSLIB0039 // só identifica o protocolo antigo que o servidor aceitou (para avisar)
        if (ssl.SslProtocol is SslProtocols.Tls or SslProtocols.Tls11)
#pragma warning restore SYSLIB0039
        {
            problems.Add($"Protocolo antigo ({Protocol(ssl.SslProtocol)}): desative TLS 1.0/1.1 no servidor.");
        }

        return new TlsReport(host, port, Protocol(ssl.SslProtocol), ssl.NegotiatedCipherSuite.ToString(), elapsed, info, chainInfos, problems.Distinct().ToList());
    }

#pragma warning disable SYSLIB0039 // nomes dos protocolos antigos, só para mostrar
    private static string Protocol(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
        _ => protocol.ToString(),
    };
#pragma warning restore SYSLIB0039

    private static CertificateInfo Describe(X509Certificate2 cert)
    {
        var names = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                names.AddRange(san.EnumerateDnsNames());
                names.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
            }
        }

        return new CertificateInfo(cert.Subject, cert.Issuer, cert.NotBefore, cert.NotAfter, cert.Thumbprint, names,
            cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "");
    }

    private static string TranslateChainStatus(X509ChainStatus status) => status.Status switch
    {
        X509ChainStatusFlags.UntrustedRoot => "Emissor (raiz) não confiável: certificado autoassinado ou de CA interna.",
        X509ChainStatusFlags.PartialChain => "Cadeia incompleta: o servidor não envia o certificado intermediário.",
        X509ChainStatusFlags.NotTimeValid => "Algum certificado da cadeia está fora da validade.",
        X509ChainStatusFlags.Revoked => "Certificado revogado.",
        X509ChainStatusFlags.NotSignatureValid => "Assinatura inválida na cadeia.",
        _ => status.StatusInformation.Trim(),
    };
}

// ==================================================================== HTTP
public sealed record HttpHop(string Url, int Status, string Reason, IReadOnlyList<KeyValuePair<string, string>> Headers, TimeSpan Elapsed);

public sealed record HttpReport(
    IReadOnlyList<HttpHop> Hops,
    TimeSpan DnsTime,
    TimeSpan ConnectTime,
    TimeSpan FirstByte,
    TimeSpan Total,
    long BodyBytes,
    string BodyPreview,
    string? RemoteAddress,
    IReadOnlyList<(string Header, bool Present, string Hint)> SecurityHeaders);

public static class HttpInspector
{
    public static async Task<HttpReport> InspectAsync(string url, string method, bool followRedirects, CancellationToken ct)
    {
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        var uri = new Uri(url);
        var dnsWatch = Stopwatch.StartNew();
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        var dns = dnsWatch.Elapsed;
        TimeSpan connect = default;
        string? remote = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = async (context, token) =>
            {
                var watch = Stopwatch.StartNew();
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    if (connect == default)
                    {
                        connect = watch.Elapsed;
                        remote = socket.RemoteEndPoint?.ToString();
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RootDock-HTTP-Inspector/1.0");
        var hops = new List<HttpHop>();
        var total = Stopwatch.StartNew();
        TimeSpan firstByte = default;
        var current = uri;
        HttpResponseMessage? response = null;
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var hopWatch = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, current);
                response?.Dispose();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (firstByte == default)
                {
                    firstByte = total.Elapsed;
                }

                var headers = response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToList();
                hops.Add(new HttpHop(current.ToString(), (int)response.StatusCode, response.ReasonPhrase ?? "", headers, hopWatch.Elapsed));
                if (!followRedirects || (int)response.StatusCode is < 300 or > 399 || response.Headers.Location is null)
                {
                    break;
                }

                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
            }

            long bytes = 0;
            var preview = "";
            if (method != "HEAD" && response is not null)
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[256 * 1024];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false)) > 0)
                {
                    read += n;
                }

                bytes = read;
                preview = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 4096));
            }

            var last = hops[^1].Headers;
            bool Has(string name) => last.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            var security = new List<(string, bool, string)>
            {
                ("Strict-Transport-Security", Has("Strict-Transport-Security"), "força HTTPS nas próximas visitas (HSTS)"),
                ("Content-Security-Policy", Has("Content-Security-Policy"), "limita de onde scripts e conteúdos podem vir"),
                ("X-Frame-Options", Has("X-Frame-Options") || Has("Content-Security-Policy"), "impede o site dentro de iframes (clickjacking)"),
                ("X-Content-Type-Options", Has("X-Content-Type-Options"), "impede o navegador de adivinhar o tipo (nosniff)"),
                ("Referrer-Policy", Has("Referrer-Policy"), "controla o endereço enviado a outros sites"),
            };
            return new HttpReport(hops, dns, connect, firstByte, total.Elapsed, bytes, preview, remote ?? addresses.FirstOrDefault()?.ToString(), security);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
