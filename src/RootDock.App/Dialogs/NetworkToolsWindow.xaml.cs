using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RootDock.App.Services;
using RootDock.Core.Network;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>
/// Ferramentas de diagnóstico de rede num só lugar: ping, traceroute, MTR, DNS (qualquer tipo de
/// registro), portas, calculadora IP, varredura de rede, tabela ARP, WHOIS, certificado SSL e HTTP.
/// Os resultados são texto (fácil de copiar para um chamado).
/// </summary>
public partial class NetworkToolsWindow : FluentWindow
{
    private static NetworkToolsWindow? _open;

    private readonly Func<string?> _activeHost;
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _run;
    private Tool? _tool;

    // Opções (criadas por ferramenta)
    private ComboBox? _countBox;
    private ComboBox? _dnsTypeBox;
    private ComboBox? _dnsServerBox;
    private CheckBox? _namesBox;
    private ComboBox? _methodBox;
    private CheckBox? _followBox;
    private System.Windows.Controls.TextBox? _portsBox;
    private ComboBox? _splitBox;

    private sealed record Tool(string Id, string Name, string Short, SymbolRegular Icon, string Description, string Placeholder, bool UsesHost = true, bool Continuous = false);

    private static readonly Tool[] Tools =
    [
        new("ping", "Ping", "o servidor responde?", SymbolRegular.Pulse20,
            "Envia pacotes ICMP e mede o tempo de resposta e a perda.", "IP ou nome, ex.: 8.8.8.8"),
        new("trace", "Traceroute", "caminho até o servidor", SymbolRegular.BranchFork20,
            "Mostra cada roteador no caminho até o destino, com o tempo de cada salto.", "IP ou nome, ex.: google.com"),
        new("mtr", "MTR (contínuo)", "perda por salto, ao vivo", SymbolRegular.DataLine20,
            "Traceroute que não para: acumula perda e tempos de cada salto para achar onde a conexão piora. Clique em Parar quando quiser.",
            "IP ou nome", Continuous: true),
        new("dns", "DNS", "A, MX, TXT, NS, SOA...", SymbolRegular.Globe20,
            "Consulta qualquer tipo de registro em qualquer servidor DNS (o do Windows ou públicos), com TTL. Um IP consulta o nome reverso (PTR).",
            "domínio ou IP, ex.: exemplo.com.br"),
        new("ports", "Portas", "quais portas estão abertas", SymbolRegular.PlugConnected20,
            "Testa conexões TCP nas portas escolhidas: aberta, fechada ou sem resposta (firewall).", "IP ou nome do servidor"),
        new("calc", "Calculadora IP", "rede, máscara, faixa", SymbolRegular.Calculator20,
            "Rede, broadcast, máscara, faixa de hosts e quantidade, a partir de IP/prefixo ou IP e máscara. Aceita IPv6.",
            "ex.: 192.168.10.77/24 ou 10.0.0.5 255.255.252.0", UsesHost: false),
        new("sweep", "Varredura de rede", "quem está ligado na rede", SymbolRegular.Search20,
            "Ping em todos os endereços da rede (até /20) e lista quem respondeu, com nome e endereço MAC (na rede local).",
            "rede, ex.: 192.168.0.0/24", UsesHost: false),
        new("arp", "Tabela ARP", "IP x MAC deste computador", SymbolRegular.TableSimple20,
            "Endereços físicos (MAC) que este computador conhece na rede local. Filtre digitando parte do IP ou do MAC.",
            "filtro opcional (IP ou MAC)", UsesHost: false),
        new("whois", "WHOIS", "dono do domínio ou IP", SymbolRegular.PersonInfo20,
            "Quem registrou o domínio ou bloco de IP, datas e servidores DNS (consulta a IANA e segue para o registro certo, ex.: registro.br).",
            "domínio ou IP, ex.: exemplo.com.br"),
        new("ssl", "Certificado SSL", "validade e cadeia", SymbolRegular.LockClosed20,
            "Conecta por TLS e mostra o certificado: para quais nomes vale, quem emitiu, dias até vencer, protocolo e problemas da cadeia.",
            "servidor[:porta], ex.: exemplo.com.br ou 10.0.0.5:8443"),
        new("http", "HTTP", "status, redirecionamentos, cabeçalhos", SymbolRegular.Code20,
            "Faz a requisição e mostra status, cada redirecionamento, tempos (DNS, conexão, primeiro byte), cabeçalhos e os de segurança.",
            "URL, ex.: https://exemplo.com.br"),
    ];

    private sealed record ToolItem(Tool Tool)
    {
        public string Name => Tool.Name;

        public string Short => Tool.Short;

        public SymbolRegular Icon => Tool.Icon;

        public override string ToString() => Name;
    }

    private NetworkToolsWindow(Func<string?> activeHost)
    {
        InitializeComponent();
        _activeHost = activeHost;
        ToolsList.ItemsSource = Tools.Select(t => new ToolItem(t)).ToList();
        ToolsList.SelectedIndex = 0;
        Closed += (_, _) =>
        {
            _run?.Cancel();
            _open = null;
        };
    }

    /// <summary>Abre (ou traz para a frente) a janela, opcionalmente numa ferramenta.</summary>
    public static void ShowFor(Window owner, Func<string?> activeHost, string? tool = null, string? host = null)
    {
        _open ??= new NetworkToolsWindow(activeHost) { Owner = owner };
        _open.Show();
        _open.Activate();
        if (tool is not null && Tools.FirstOrDefault(t => t.Id == tool) is { } chosen)
        {
            _open.ToolsList.SelectedIndex = Array.IndexOf(Tools, chosen);
        }

        if (host is not null && _open._tool is { UsesHost: true })
        {
            _open.InputBox.Text = host;
            _open.InputBox.SelectAll();
        }
    }

    // ------------------------------------------------------------------ troca de ferramenta
    private void OnToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolsList.SelectedItem is not ToolItem item)
        {
            return;
        }

        _run?.Cancel();
        _tool = item.Tool;
        ToolTitle.Text = _tool.Name;
        ToolDescription.Text = _tool.Description;
        InputBox.PlaceholderText = _tool.Placeholder;
        ActiveHostButton.Visibility = _tool.UsesHost ? Visibility.Visible : Visibility.Collapsed;
        BuildOptions();
        SetOutput("");
        StatusText.Text = "";
        if (_tool.Id == "sweep" && InputBox.Text.Length == 0)
        {
            InputBox.Text = LocalNetwork() ?? "";
        }

        if (_tool.UsesHost && InputBox.Text.Length == 0 && _activeHost() is { } host)
        {
            InputBox.Text = host;
        }

        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void BuildOptions()
    {
        OptionsPanel.Children.Clear();
        NoteText.Visibility = Visibility.Collapsed;
        switch (_tool!.Id)
        {
            case "ping":
                _countBox = Combo(["4 pacotes", "10 pacotes", "Contínuo (até parar)"], 0, 190);
                Add("Quantidade", _countBox);
                break;
            case "trace":
                _namesBox = Check("Mostrar nomes dos roteadores", true);
                OptionsPanel.Children.Add(_namesBox);
                break;
            case "dns":
                _dnsTypeBox = Combo(["A", "AAAA", "MX", "TXT", "NS", "CNAME", "SOA", "SRV", "CAA", "PTR"], 0, 110);
                var system = DnsClient.SystemServer();
                _dnsServerBox = Combo(
                    [system is null ? "Do Windows" : $"Do Windows ({system})", "Cloudflare (1.1.1.1)", "Google (8.8.8.8)", "Quad9 (9.9.9.9)"], 0, 230);
                _dnsServerBox.IsEditable = true;
                _dnsServerBox.ToolTip = "Escolha ou digite o IP de outro servidor DNS";
                Add("Tipo", _dnsTypeBox);
                Add("Servidor", _dnsServerBox);
                break;
            case "ports":
                _portsBox = new System.Windows.Controls.TextBox { Text = "comuns", Width = 300, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), ToolTip = "\"comuns\" (as ~50 mais usadas), ou lista e faixas: 22,80,443,8000-8100" };
                Add("Portas", _portsBox);
                NoteText.Text = "Use só em servidores e redes que você administra: varrer portas de terceiros pode ser considerado ataque.";
                NoteText.Visibility = Visibility.Visible;
                break;
            case "calc":
                _splitBox = Combo(["Não dividir", "/25", "/26", "/27", "/28", "/29", "/30"], 0, 150);
                Add("Dividir em", _splitBox);
                break;
            case "sweep":
                _namesBox = Check("Buscar nomes (DNS reverso)", true);
                OptionsPanel.Children.Add(_namesBox);
                NoteText.Text = "Use só em redes que você administra. Equipamentos com firewall que bloqueia ping não aparecem.";
                NoteText.Visibility = Visibility.Visible;
                break;
            case "http":
                _methodBox = Combo(["GET", "HEAD"], 0, 100);
                _followBox = Check("Seguir redirecionamentos", true);
                Add("Método", _methodBox);
                OptionsPanel.Children.Add(_followBox);
                break;
        }
    }

    private void Add(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        panel.Children.Add(control);
        OptionsPanel.Children.Add(panel);
    }

    private static ComboBox Combo(string[] items, int selected, double width) =>
        new() { ItemsSource = items, SelectedIndex = selected, Width = width };

    private static CheckBox Check(string text, bool value) =>
        new() { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 18, 6), VerticalAlignment = VerticalAlignment.Center };

    private void OnUseActiveHost(object sender, RoutedEventArgs e)
    {
        if (_activeHost() is { } host)
        {
            InputBox.Text = host;
            InputBox.Focus();
        }
        else
        {
            StatusText.Text = "Nenhum terminal SSH/Telnet ativo.";
        }
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnRun(sender, e);
        }
    }

    // ------------------------------------------------------------------ execução
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_tool is null || _run is not null)
        {
            return;
        }

        var input = InputBox.Text.Trim();
        if (input.Length == 0 && _tool.Id is not ("arp"))
        {
            StatusText.Text = "Preencha o campo acima.";
            InputBox.Focus();
            return;
        }

        _run = new CancellationTokenSource();
        var ct = _run.Token;
        SetBusy(true);
        SetOutput("");
        var watch = Stopwatch.StartNew();
        try
        {
            switch (_tool.Id)
            {
                case "ping": await PingAsync(input, ct); break;
                case "trace": await TraceAsync(input, ct); break;
                case "mtr": await MtrAsync(input, ct); break;
                case "dns": await DnsAsync(input, ct); break;
                case "ports": await PortsAsync(input, ct); break;
                case "calc": Calculate(input); break;
                case "sweep": await SweepAsync(input, ct); break;
                case "arp": Arp(input); break;
                case "whois": await WhoisAsync(input, ct); break;
                case "ssl": await SslAsync(input, ct); break;
                case "http": await HttpAsync(input, ct); break;
            }

            if (!ct.IsCancellationRequested)
            {
                StatusText.Text = $"Concluído em {watch.Elapsed.TotalSeconds:0.0} s.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Interrompido.";
        }
        catch (Exception ex)
        {
            Log.Info($"Ferramenta de rede {_tool.Id}: {ex.Message}");
            Line();
            Line("Erro: " + Explain(ex));
            StatusText.Text = "Falhou.";
        }
        finally
        {
            _run?.Dispose();
            _run = null;
            SetBusy(false);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _run?.Cancel();

    private void SetBusy(bool busy)
    {
        RunButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ToolsList.IsEnabled = !busy;
        if (busy)
        {
            StatusText.Text = "Executando...";
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound } => "nome não encontrado no DNS.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "conexão recusada (porta fechada).",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "sem resposta (tempo esgotado).",
        SocketException s => s.Message,
        HttpRequestException { InnerException: { } inner } => inner.Message,
        System.Security.Authentication.AuthenticationException a => "falha no TLS: " + (a.InnerException?.Message ?? a.Message),
        _ => ex.Message,
    };

    // ------------------------------------------------------------------ saída
    /// <summary>Ping contínuo esquecido aberto por horas: guarda só as últimas linhas.</summary>
    private const int MaxLines = 5000;
    private const int TrimLines = 1000;
    private int _lines;

    private void SetOutput(string text)
    {
        _output.Clear().Append(text);
        OutputBox.Text = text;
        _lines = text.Count(c => c == '\n');
    }

    private void Line(string text = "")
    {
        _output.Append(text).Append('\n');
        OutputBox.AppendText(text + "\n");
        if (++_lines > MaxLines)
        {
            var all = _output.ToString();
            var cut = 0;
            for (var i = 0; i < TrimLines && cut < all.Length; i++)
            {
                var next = all.IndexOf('\n', cut);
                cut = next < 0 ? all.Length : next + 1;
            }

            SetOutput("[... linhas antigas descartadas ...]\n" + all[cut..]);
        }

        OutputBox.ScrollToEnd();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length > 0)
        {
            SafeClipboard.SetText(OutputBox.Text);
            StatusText.Text = "Resultado copiado.";
        }
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    // ------------------------------------------------------------------ ferramentas
    private async Task PingAsync(string host, CancellationToken ct)
    {
        var address = await Tracer.ResolveAsync(host, ct);
        var count = _countBox!.SelectedIndex switch { 0 => 4, 1 => 10, _ => int.MaxValue };
        Line($"Ping em {host}{(host == address.ToString() ? "" : $" [{address}]")}:");
        Line();
        var times = new List<long>();
        var sent = 0;
        using var ping = new Ping();
        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2), new byte[32], null, ct);
                if (reply.Status == IPStatus.Success)
                {
                    times.Add(reply.RoundtripTime);
                    Line($"Resposta de {reply.Address}: tempo={reply.RoundtripTime} ms TTL={reply.Options?.Ttl.ToString() ?? "?"}");
                }
                else
                {
                    Line(reply.Status switch
                    {
                        IPStatus.TimedOut => "Esgotado o tempo limite.",
                        IPStatus.DestinationHostUnreachable => "Host de destino inacessível.",
                        IPStatus.DestinationNetworkUnreachable => "Rede de destino inacessível.",
                        _ => $"Falha: {reply.Status}",
                    });
                }
            }
            catch (PingException ex)
            {
                Line("Falha: " + (ex.InnerException?.Message ?? ex.Message));
            }

            if (i + 1 < count)
            {
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Line();
        var lost = sent - times.Count;
        Line($"Pacotes: enviados {sent}, recebidos {times.Count}, perdidos {lost} ({(sent == 0 ? 0 : 100.0 * lost / sent):0.#}% de perda)");
        if (times.Count > 0)
        {
            Line($"Tempo: mínimo {times.Min()} ms, médio {times.Average():0} ms, máximo {times.Max()} ms");
        }
    }

    private async Task TraceAsync(string host, CancellationToken ct)
    {
        var address = await Tracer.ResolveAsync(host, ct);
        Line($"Rota até {host} [{address}], no máximo 30 saltos:");
        Line();
        await Tracer.TraceAsync(host, 30, TimeSpan.FromSeconds(2), _namesBox!.IsChecked == true, hop => Ui(() =>
        {
            var times = string.Join("  ", hop.Times.Select(t => t is null ? "    *" : $"{t,4} ms"));
            var where = hop.Address is null ? "Esgotado o tempo limite." : hop.HostName is null ? hop.Address.ToString() : $"{hop.HostName} [{hop.Address}]";
            Line($"{hop.Ttl,3}  {times}  {where}");
            if (hop.Reached)
            {
                Line();
                Line("Destino alcançado.");
            }
        }), ct);
    }

    private async Task MtrAsync(string host, CancellationToken ct)
    {
        var address = await Tracer.ResolveAsync(host, ct);
        await Mtr.RunAsync(host, 30, TimeSpan.FromSeconds(1), hops => Ui(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MTR até {host} [{address}] — atualiza a cada segundo (Parar para encerrar)");
            sb.AppendLine();
            sb.AppendLine($"{"Salto",5}  {"Endereço",-46} {"Perda",7} {"Env.",5} {"Últ.",6} {"Méd.",6} {"Melhor",7} {"Pior",6}");
            foreach (var h in hops)
            {
                var where = h.Address is null ? "???" : h.HostName is null ? h.Address.ToString() : $"{h.HostName} ({h.Address})";
                if (where.Length > 46)
                {
                    where = where[..45] + "…";
                }

                var best = h.Received == 0 ? "-" : h.Best.ToString();
                sb.AppendLine($"{h.Ttl,5}  {where,-46} {h.Loss,6:0.0}% {h.Sent,5} {(h.Received == 0 ? "-" : h.Last.ToString()),6} {(h.Received == 0 ? "-" : h.Average.ToString("0")),6} {best,7} {(h.Received == 0 ? "-" : h.Worst.ToString()),6}");
            }

            sb.AppendLine();
            sb.AppendLine("Perda só num salto do meio (e não no final) costuma ser o roteador limitando ICMP, não problema real.");
            SetOutput(sb.ToString());
        }), ct);
    }

    private async Task DnsAsync(string query, CancellationToken ct)
    {
        var type = Enum.Parse<DnsType>((string)_dnsTypeBox!.SelectedItem!);
        var serverText = _dnsServerBox!.Text.Trim();
        var server = serverText.StartsWith("Cloudflare") ? IPAddress.Parse("1.1.1.1")
            : serverText.StartsWith("Google") ? IPAddress.Parse("8.8.8.8")
            : serverText.StartsWith("Quad9") ? IPAddress.Parse("9.9.9.9")
            : serverText.StartsWith("Do Windows") ? DnsClient.SystemServer() ?? IPAddress.Parse("1.1.1.1")
            : IPAddress.TryParse(serverText, out var custom) ? custom
            : throw new FormatException("Servidor DNS inválido: use um da lista ou digite um IP.");

        var name = query;
        if (IPAddress.TryParse(query, out var ip))
        {
            name = DnsClient.ReverseName(ip);
            type = DnsType.PTR;
        }

        var response = await DnsClient.QueryAsync(name, type, server, TimeSpan.FromSeconds(5), ct);
        Line($"Consulta {type} de {name}");
        Line($"Servidor: {response.Server}   Tempo: {response.Elapsed.TotalMilliseconds:0} ms   Resposta: {response.Status}{(response.Authoritative ? " (autoritativa)" : "")}");
        Line();
        if (response.Answers.Count > 0)
        {
            Line($"{"Nome",-40} {"TTL",8}  {"Tipo",-6} Valor");
            foreach (var r in response.Answers)
            {
                Line($"{r.Name,-40} {FormatTtl(r.Ttl),8}  {r.TypeName,-6} {r.Data}");
            }
        }

        if (response.Authority.Count > 0)
        {
            Line();
            Line("Autoridade:");
            foreach (var r in response.Authority)
            {
                Line($"{r.Name,-40} {FormatTtl(r.Ttl),8}  {r.TypeName,-6} {r.Data}");
            }
        }
    }

    private static string FormatTtl(int ttl) =>
        ttl >= 86400 ? $"{ttl / 86400}d" : ttl >= 3600 ? $"{ttl / 3600}h" : ttl >= 60 ? $"{ttl / 60}min" : $"{ttl}s";

    private async Task PortsAsync(string host, CancellationToken ct)
    {
        var ports = PortList.Parse(_portsBox!.Text);
        if (ports.Count > 5000)
        {
            throw new FormatException("Até 5000 portas por vez.");
        }

        Line($"Testando {ports.Count} porta(s) TCP em {host}...");
        Line();
        var results = new List<PortResult>();
        var done = 0;
        // O Windows tenta de novo quando a porta recusa (~2 s): com menos tempo, "fechada" pareceria "sem resposta".
        await PortScanner.ScanAsync(host, ports, TimeSpan.FromSeconds(3), 96, result =>
        {
            lock (results)
            {
                results.Add(result);
            }

            var n = Interlocked.Increment(ref done);
            if (result.State == "aberta")
            {
                Ui(() => Line($"{result.Port,6}/tcp  aberta   {result.Service,-18} {result.Elapsed?.TotalMilliseconds:0} ms"));
            }

            if (n % 20 == 0 || n == ports.Count)
            {
                Ui(() => StatusText.Text = $"{n} de {ports.Count} portas testadas...");
            }
        }, ct);

        var open = results.Count(r => r.State == "aberta");
        var closed = results.Count(r => r.State == "fechada");
        var filtered = results.Count - open - closed;
        Line();
        Line($"Resumo: {open} aberta(s), {closed} fechada(s), {filtered} sem resposta (provável firewall).");
        if (ports.Count <= 60)
        {
            Line();
            foreach (var r in results.Where(r => r.State != "aberta").OrderBy(r => r.Port))
            {
                Line($"{r.Port,6}/tcp  {r.State,-24} {r.Service}");
            }
        }
    }

    private void Calculate(string input)
    {
        var n = IpCalculator.Parse(input);
        Line($"Endereço         {n.Address}");
        Line($"Rede             {n.Cidr}");
        if (!n.IsIPv6)
        {
            Line($"Máscara          {n.Mask}  (/{n.Prefix})");
            Line($"Curinga          {n.Wildcard}");
            Line($"Broadcast        {n.Broadcast}");
        }

        Line($"Primeiro host    {n.FirstHost}");
        Line($"Último host      {n.LastHost}");
        Line($"Endereços        {n.TotalAddresses:N0}");
        if (!n.IsIPv6)
        {
            Line($"Hosts utilizáveis {n.UsableHosts:N0}");
        }

        Line($"Tipo             {n.Kind}");
        if (_splitBox!.SelectedIndex > 0 && !n.IsIPv6)
        {
            var prefix = int.Parse(((string)_splitBox.SelectedItem!)[1..]);
            var parts = IpCalculator.Split(n, prefix);
            Line();
            if (parts.Count == 0)
            {
                Line($"Escolha um prefixo maior que /{n.Prefix} para dividir.");
            }
            else
            {
                Line($"Dividida em /{prefix} ({parts.Count}{(parts.Count == 256 ? "+" : "")} sub-redes):");
                foreach (var p in parts)
                {
                    Line($"  {p.Cidr,-20} hosts {p.FirstHost} – {p.LastHost}   broadcast {p.Broadcast}");
                }
            }
        }
    }

    private async Task SweepAsync(string input, CancellationToken ct)
    {
        var network = IpCalculator.Parse(input.Contains('/') || input.Contains(' ') ? input : input + "/24");
        Line($"Varrendo {network.Cidr} ({network.UsableHosts:N0} endereços)...");
        Line();
        Line($"{"Endereço",-16} {"Tempo",7}  {"MAC",-18} Nome");
        var found = new List<SweepResult>();
        await SubnetScanner.ScanAsync(network, TimeSpan.FromMilliseconds(900), 96, _namesBox!.IsChecked == true,
            result =>
            {
                lock (found)
                {
                    found.Add(result);
                }

                Ui(() => Line($"{result.Address,-16} {result.RoundtripMs,5} ms  {result.Mac ?? "",-18} {result.HostName}"));
            },
            (done, total) => Ui(() => StatusText.Text = $"{done} de {total} endereços..."), ct);
        Line();
        Line($"{found.Count} equipamento(s) responderam.");
    }

    private void Arp(string filter)
    {
        var all = ArpTable.Read();
        // Multicast (224.0.0.0/4) e broadcast não são equipamentos: ficam de fora.
        var entries = all
            .Where(e => e.Address.GetAddressBytes() is var b && b.Length == 4 && b[0] < 224 && e.Mac != "FF-FF-FF-FF-FF-FF")
            .Where(e => filter.Length == 0 || e.Address.ToString().Contains(filter) || e.Mac.Contains(filter.Replace(':', '-'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.InterfaceIndex).ThenBy(e => IpCalculatorKey(e.Address))
            .ToList();
        Line($"{"Endereço IP",-18} {"Endereço físico (MAC)",-20} {"Tipo",-10} Interface");
        foreach (var e in entries)
        {
            Line($"{e.Address,-18} {e.Mac,-20} {e.Type,-10} {e.InterfaceIndex}");
        }

        Line();
        Line($"{entries.Count} equipamento(s) conhecido(s) na rede local ({all.Count - entries.Count} de multicast/broadcast ocultas).");
    }

    private static uint IpCalculatorKey(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 ? ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3] : 0;
    }

    private async Task WhoisAsync(string query, CancellationToken ct)
    {
        var results = await Whois.LookupAsync(query, ct);
        foreach (var (server, text) in results)
        {
            Line($"===== {server} =====");
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                Line(line);
            }

            Line();
        }
    }

    private async Task SslAsync(string input, CancellationToken ct)
    {
        var host = input;
        var port = 443;
        if (input.Contains("://"))
        {
            var uri = new Uri(input);
            host = uri.Host;
            port = uri.IsDefaultPort ? 443 : uri.Port;
        }
        else if (input.LastIndexOf(':') is var colon and > 0 && !input.Contains("::") && int.TryParse(input[(colon + 1)..], out var p))
        {
            host = input[..colon];
            port = p;
        }

        var report = await TlsInspector.InspectAsync(host, port, ct);
        var c = report.Certificate;
        Line($"{host}:{port}   {report.Protocol}   {report.Cipher}   ({report.Elapsed.TotalMilliseconds:0} ms)");
        Line();
        Line(report.Problems.Count == 0 ? "✓ Nenhum problema encontrado." : "Problemas:");
        foreach (var problem in report.Problems)
        {
            Line("  ✗ " + problem);
        }

        Line();
        Line($"Para               {c.Subject}");
        Line($"Nomes válidos      {(c.Names.Count == 0 ? "(nenhum SAN)" : string.Join(", ", c.Names))}");
        Line($"Emitido por        {c.Issuer}");
        Line($"Válido de          {c.NotBefore:dd/MM/yyyy HH:mm} até {c.NotAfter:dd/MM/yyyy HH:mm}  ({(c.DaysLeft >= 0 ? $"faltam {c.DaysLeft} dias" : $"VENCIDO há {-c.DaysLeft} dias")})");
        Line($"Chave              {c.KeyAlgorithm}");
        Line($"Impressão (SHA-1)  {c.Thumbprint}");
        if (report.Chain.Count > 1)
        {
            Line();
            Line("Cadeia:");
            for (var i = 0; i < report.Chain.Count; i++)
            {
                var item = report.Chain[i];
                Line($"  {new string(' ', i * 2)}{(i == 0 ? "" : "└ ")}{item.Subject}   (até {item.NotAfter:dd/MM/yyyy})");
            }
        }
    }

    private async Task HttpAsync(string url, CancellationToken ct)
    {
        var report = await HttpInspector.InspectAsync(url, (string)_methodBox!.SelectedItem!, _followBox!.IsChecked == true, ct);
        Line($"Servidor: {report.RemoteAddress}");
        Line($"Tempos: DNS {report.DnsTime.TotalMilliseconds:0} ms · conexão {report.ConnectTime.TotalMilliseconds:0} ms · primeiro byte {report.FirstByte.TotalMilliseconds:0} ms · total {report.Total.TotalMilliseconds:0} ms");
        Line();
        for (var i = 0; i < report.Hops.Count; i++)
        {
            var hop = report.Hops[i];
            Line($"{(i == 0 ? "" : "→ ")}{hop.Status} {hop.Reason}   {hop.Url}   ({hop.Elapsed.TotalMilliseconds:0} ms)");
        }

        var last = report.Hops[^1];
        Line();
        Line("Cabeçalhos da resposta:");
        foreach (var (key, value) in last.Headers)
        {
            Line($"  {key}: {value}");
        }

        Line();
        Line("Segurança:");
        foreach (var (header, present, hint) in report.SecurityHeaders)
        {
            Line($"  {(present ? "✓" : "✗")} {header,-27} {hint}");
        }

        if (report.BodyPreview.Length > 0)
        {
            Line();
            Line($"Conteúdo ({report.BodyBytes:N0} bytes lidos, primeiros 4 KB):");
            Line(report.BodyPreview);
        }
    }

    /// <summary>Rede local (IPv4 e prefixo) da interface com gateway, para a varredura.</summary>
    private static string? LocalNetwork()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    continue;
                }

                var v4 = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 is not null)
                {
                    return IpCalculator.Parse($"{v4.Address}/{Math.Max(v4.PrefixLength, 20)}").Cidr;
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return null;
    }
}
