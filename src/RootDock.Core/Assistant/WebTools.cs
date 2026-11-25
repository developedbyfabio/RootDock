using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RootDock.Core.Assistant;

/// <summary>
/// Busca na web e leitura de páginas para o assistente consultar documentação. Roda neste
/// computador (não no servidor da sessão), sem chave: a busca usa a página simples do DuckDuckGo.
/// Endereços da rede interna e deste computador são recusados, para que um texto malicioso lido
/// no servidor não consiga usar o assistente para alcançá-los.
/// </summary>
public sealed partial class WebTools(HttpClient http)
{
    private const int MaxResults = 8;
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int PageChars = 30_000;
    private const int MaxRedirects = 5;

    /// <summary>Página montada para custar caro às expressões regulares não prende a resposta.</summary>
    private const int RegexTimeout = 4000;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Endereços de busca, tentados em ordem: o buscador às vezes recusa pedidos automáticos numa
    /// das versões da página e atende na outra.
    /// </summary>
    public IReadOnlyList<string> SearchUrls { get; init; } = ["https://html.duckduckgo.com/html/", "https://lite.duckduckgo.com/lite/"];

    /// <summary>Recusar endereços internos (desligado só nos testes, que usam um servidor local).</summary>
    public bool BlockPrivateAddresses { get; init; } = true;

    /// <summary>Um cliente HTTP próprio: sem redirecionamento automático (cada salto é conferido) e sem cookies.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectPublicAsync,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Conecta no endereço que foi conferido, e não em outro que o nome passe a resolver um instante
    /// depois (um site malicioso pode responder um IP público na conferência e 127.0.0.1 na conexão).
    /// Com proxy configurado no Windows a conexão é com o proxy, que é quem resolve o nome: aí vale
    /// só a conferência feita antes do pedido.
    /// </summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var direct = context.InitialRequestMessage.RequestUri is { } target && string.Equals(target.IdnHost, host, StringComparison.OrdinalIgnoreCase);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var address = direct ? addresses.FirstOrDefault(a => !IsPrivate(a)) : addresses.FirstOrDefault();
        if (address is null)
        {
            throw new HttpRequestException(addresses.Length == 0 ? "nome não encontrado" : "endereço da rede interna ou deste computador");
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        IReadOnlyList<SearchResult> results = [];
        var refused = false;
        foreach (var url in SearchUrls)
        {
            try
            {
                var html = (await GetAsync(new Uri(url + "?q=" + Uri.EscapeDataString(query)), checkAddress: false, ct).ConfigureAwait(false)).Text;
                results = ParseResults(html);
                if (results.Count > 0)
                {
                    break;
                }

                refused |= html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) || html.Contains("challenge", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                refused = true; // 403, 429 ou sem resposta: tenta o próximo endereço
            }
        }

        if (results.Count == 0)
        {
            return refused
                ? throw new IOException("O buscador recusou o pedido agora (ele limita buscas automáticas). Não espere nem insista: abra direto a documentação oficial com web_fetch, ou siga sem a busca e avise a pessoa.")
                : "Nenhum resultado. Tente outras palavras.";
        }

        var text = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            text.Append(i + 1).Append(". ").Append(results[i].Title).Append('\n').Append("   ").Append(results[i].Url).Append('\n');
            if (results[i].Snippet.Length > 0)
            {
                text.Append("   ").Append(results[i].Snippet).Append('\n');
            }
        }

        return text.Append("[Abra a página que interessa com web_fetch para ler o conteúdo.]").ToString();
    }

    public async Task<string> FetchAsync(string url, int offset, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new IOException("url precisa ser um endereço http ou https completo.");
        }

        var (text, type, final) = await GetAsync(uri, BlockPrivateAddresses, ct).ConfigureAwait(false);
        string content;
        try
        {
            content = type.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(text) ? HtmlToText(text, final) : text.Replace("\r\n", "\n");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new IOException("A página é grande ou malformada demais para ler.");
        }
        if (content.Length == 0)
        {
            return "A página veio sem texto (provavelmente é montada por JavaScript).";
        }

        offset = Math.Clamp(offset, 0, content.Length);
        var end = Math.Min(content.Length, offset + PageChars);
        var head = final != uri ? $"[redirecionado para {final}]\n" : "";
        var tail = end < content.Length ? $"\n[mostrados os caracteres {offset} a {end} de {content.Length}: continue com offset {end}]" : "";
        return head + content[offset..end] + tail;
    }

    internal sealed record SearchResult(string Title, string Url, string Snippet);

    internal static IReadOnlyList<SearchResult> ParseResults(string html)
    {
        var results = new List<SearchResult>();
        var snippets = ResultSnippet().Matches(html);
        var links = AnyLink().Matches(html).Where(m => ResultClass().IsMatch(m.Groups["attrs"].Value)).ToList();
        for (var i = 0; i < links.Count && results.Count < MaxResults; i++)
        {
            var url = Href().Match(links[i].Groups["attrs"].Value) is { Success: true } href ? TargetOf(WebUtility.HtmlDecode(href.Groups[1].Value)) : null;
            if (url is null || url.Contains("duckduckgo.com/y.js", StringComparison.Ordinal))
            {
                continue; // anúncio
            }

            // O trecho de um resultado vem depois do link dele e antes do link seguinte.
            var next = i + 1 < links.Count ? links[i + 1].Index : int.MaxValue;
            var snippet = snippets.FirstOrDefault(s => s.Index > links[i].Index && s.Index < next);
            results.Add(new SearchResult(Plain(links[i].Groups["title"].Value), url, snippet is null ? "" : Plain(snippet.Groups["text"].Value)));
        }

        return results;
    }

    /// <summary>O DuckDuckGo embrulha o destino em /l/?uddg=...; devolve o endereço de verdade.</summary>
    private static string? TargetOf(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/l/")
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                if (pair.StartsWith("uddg=", StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair[5..]);
                }
            }

            return null;
        }

        return uri.ToString();
    }

    private async Task<(string Text, string ContentType, Uri Final)> GetAsync(Uri uri, bool checkAddress, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (checkAddress)
                {
                    await RefusePrivateAsync(uri, limit.Token).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,text/plain;q=0.9,*/*;q=0.5");
                request.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.8");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new IOException("A página redireciona vezes demais.");
                    }

                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (uri.Scheme is not ("http" or "https"))
                    {
                        throw new IOException("A página redireciona para um endereço que não é http.");
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new IOException($"O site respondeu {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("json", StringComparison.OrdinalIgnoreCase) && !type.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"O endereço não é uma página de texto (tipo {type}).");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while (buffer.Length < MaxBytes && (read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }

                var encoding = Encoding.UTF8;
                if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
                {
                    try
                    {
                        encoding = Encoding.GetEncoding(charset.Trim('"'));
                    }
                    catch (ArgumentException)
                    {
                        // codificação desconhecida: fica UTF-8
                    }
                }

                return (encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), type, uri);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"O site não respondeu em {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("Não foi possível abrir o endereço: " + ex.Message, ex);
        }
    }

    private static async Task RefusePrivateAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return; // nome que este computador não resolve (rede com proxy): o pedido dirá
            }
        }

        if (addresses.Any(IsPrivate))
        {
            throw new IOException("Endereço da rede interna ou deste computador: o assistente só abre páginas da internet.");
        }
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 0 || b[0] == 127 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) ||
               (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    private static bool LooksLikeHtml(string text)
    {
        var start = text.AsSpan(0, Math.Min(text.Length, 600)).ToString();
        return start.Contains("<html", StringComparison.OrdinalIgnoreCase) || start.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Página em texto corrido: sem scripts e menus, com títulos, listas, blocos de código e links.</summary>
    internal static string HtmlToText(string html, Uri page)
    {
        var title = TitleTag().Match(html) is { Success: true } found ? Plain(found.Groups[1].Value) : "";
        var body = Dropped().Replace(html, " ");
        body = Comment().Replace(body, " ");
        // Blocos de código ficam como estão (a indentação importa); voltam ao texto no fim.
        var code = new List<string>();
        body = Pre().Replace(body, m =>
        {
            code.Add(WebUtility.HtmlDecode(AnyTag().Replace(m.Groups[1].Value, "")).Replace("\r", "").Trim('\n'));
            return $"\n\u0001{code.Count - 1}\u0001\n";
        });
        body = Anchor().Replace(body, m =>
        {
            var label = Plain(m.Groups["text"].Value);
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            if (label.Length == 0)
            {
                return " ";
            }

            return !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
                   Uri.TryCreate(page, href, out var target) && target.Scheme is "http" or "https"
                ? $"[{label}]({target})"
                : label;
        });
        body = Heading().Replace(body, "\n\n# ");
        body = ListItem().Replace(body, "\n- ");
        body = BlockBreak().Replace(body, "\n");
        body = AnyTag().Replace(body, "");
        body = WebUtility.HtmlDecode(body).Replace("\r", "").Replace(' ', ' ');
        body = TrailingSpace().Replace(body, "\n");
        body = ManySpaces().Replace(body, " ");
        body = ManyLines().Replace(body, "\n\n").Trim();
        body = CodeMark().Replace(body, m => "```\n" + code[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] + "\n```");
        return title.Length > 0 ? $"{title}\n{page}\n\n{body}" : body;
    }

    private static string Plain(string html) =>
        ManySpaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, "")).Replace('\n', ' ').Replace('\r', ' ').Replace(' ', ' '), " ").Trim();

    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex AnyLink();

    [GeneratedRegex("""\bclass\s*=\s*["'][^"']*\bresult(?:__a|-link)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex ResultClass();

    [GeneratedRegex("""\bhref\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex("""<(?<tag>a|td|div)\b[^>]*\bclass\s*=\s*["'][^"']*\bresult(?:__|-)snippet\b[^"']*["'][^>]*>(?<text>.*?)</\k<tag>\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex ResultSnippet();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<(script|style|noscript|svg|template|iframe|head|nav|footer|button|select)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex Dropped();

    [GeneratedRegex(@"<pre\b[^>]*>(.*?)</pre\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex Pre();

    [GeneratedRegex(@"\x01(\d+)\x01")]
    private static partial Regex CodeMark();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex Comment();

    [GeneratedRegex("""<a\b[^>]*?\bhref\s*=\s*["'](?<href>[^"']*)["'][^>]*>(?<text>.*?)</a\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<h[1-6]\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</?(p|div|br|tr|table|ul|ol|pre|section|article|header|main|aside|blockquote|dd|dt|dl|hr|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ManySpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyLines();
}
