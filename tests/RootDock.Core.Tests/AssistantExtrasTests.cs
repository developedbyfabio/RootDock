using System.Net;
using System.Text;
using System.Text.Json;
using RootDock.Core.Assistant;
using RootDock.Core.MultiExec;

namespace RootDock.Core.Tests;

/// <summary>Lista de tarefas, web, imagens, desfazer e lixeira do histórico do assistente.</summary>
public sealed class AssistantExtrasTests
{
    private static ToolCall Call(string name, object input) => new("c1", name, JsonSerializer.SerializeToElement(input));

    [Fact]
    public async Task UpdateTasks_ShowsTheWholeListAndNeverAsks()
    {
        var host = new Host();
        var tools = new AssistantTools(host);
        var result = await tools.ExecuteAsync(Call(AssistantTools.UpdateTasks, new
        {
            tasks = new object[]
            {
                new { title = "Criar o banco", status = "done" },
                new { title = "Criar as rotas", status = "in_progress" },
                new { title = "Testar", status = "qualquer" },
            },
        }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("1 de 3", result.Content);
        Assert.Equal(["done", "in_progress", "pending"], host.Tasks.Select(t => t.Status));
        var check = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.UpdateTasks)!, default, AssistantMode.Ask);
        Assert.Equal(PermissionKind.Allow, check.Kind);
    }

    [Fact]
    public async Task FileTools_ReportThePreviousContentBeforeChanging()
    {
        var host = new Host();
        host.Files["/srv/a.txt"] = Encoding.UTF8.GetBytes("um\ndois\n");
        var tools = new AssistantTools(host);

        await tools.ExecuteAsync(Call(AssistantTools.EditFile, new { path = "/srv/a.txt", old_text = "dois", new_text = "três" }), CancellationToken.None);
        await tools.ExecuteAsync(Call(AssistantTools.WriteFile, new { path = "/srv/novo.txt", content = "oi" }), CancellationToken.None);
        await tools.ExecuteAsync(Call(AssistantTools.WriteFile, new { path = "/srv/a.txt", content = "tudo novo" }), CancellationToken.None);

        Assert.Equal(3, host.Changes.Count);
        Assert.Equal("um\ndois\n", Encoding.UTF8.GetString(host.Changes[0].Before!));
        Assert.Equal(("/srv/novo.txt", null), (host.Changes[1].Path, host.Changes[1].Before));
        Assert.Equal("um\ntrês\n", Encoding.UTF8.GetString(host.Changes[2].Before!));
    }

    [Fact]
    public void CheckpointStore_RoundTripsAndMovesToTrash()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-cp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssistantCheckpointStore(directory);
            store.Save("conv1", "turn1", [new FileCheckpoint("/a", [1, 2, 3]), new FileCheckpoint("/b", null)]);

            var loaded = store.Load("conv1", "turn1")!;
            Assert.Equal([1, 2, 3], loaded[0].Before!);
            Assert.Null(loaded[1].Before);
            Assert.Null(store.Load("conv1", "outra"));
            Assert.Throws<ArgumentException>(() => store.Load("..", "turn1"));

            store.MoveToTrash("conv1");
            Assert.Null(store.Load("conv1", "turn1"));
            Assert.True(File.Exists(Path.Combine(directory, AssistantHistoryStore.TrashFolder, "conv1", "turn1.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HistoryDelete_MovesTheConversationToTheTrashFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssistantHistoryStore(directory);
            store.Save(new AssistantHistoryRecord { Id = "abc123", Server = "h:22", Title = "Conversa" });
            Assert.Single(store.List("h:22"));

            Assert.True(store.Delete("abc123"));
            Assert.Empty(store.List("h:22"));
            Assert.Null(store.Load("abc123"));
            Assert.True(File.Exists(Path.Combine(directory, AssistantHistoryStore.TrashFolder, "abc123.json")));
            Assert.False(store.Delete("abc123"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HistoryList_ReadsOnlyTheHeaderAndStaysFastWithBigConversations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssistantHistoryStore(directory);
            using var big = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "toolEnd", output = new string('x', 20_000) }));
            var events = Enumerable.Repeat(big.RootElement.Clone(), 100).ToList(); // ~2 MB por conversa
            for (var i = 0; i < 60; i++)
            {
                store.Save(new AssistantHistoryRecord
                {
                    Id = "c" + i,
                    Server = i % 2 == 0 ? "h:22" : "outro:22",
                    Title = "Conversa \"com aspas\" e acentuação " + i,
                    Updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    Events = events,
                });
            }

            File.WriteAllText(Path.Combine(directory, "quebrado.json"), "{\"id\": 5, \"server\"");
            var cold = System.Diagnostics.Stopwatch.StartNew();
            store.List("h:22"); // a primeira leitura paga o antivírus conferindo arquivos recém-gravados
            cold.Stop();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var list = store.List("h:22");
            watch.Stop();
            TestContext.Current.SendDiagnosticMessage($"lista fria {cold.ElapsedMilliseconds} ms, quente {watch.ElapsedMilliseconds} ms");

            Assert.Equal(30, list.Count);
            Assert.Equal("c58", list[0].Id);
            Assert.Equal("Conversa \"com aspas\" e acentuação 58", list[0].Title);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 58, 0, DateTimeKind.Utc), list[0].Updated.ToUniversalTime());
            Assert.True(watch.ElapsedMilliseconds < 500, $"listar 60 conversas de 2 MB levou {watch.ElapsedMilliseconds} ms (fria: {cold.ElapsedMilliseconds} ms)");
            Assert.Equal(100, store.Load("c58")!.Events.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Transcript_MentionsImagesThatAreNoLongerAvailable()
    {
        using var document = JsonDocument.Parse("""[{"type":"user","text":"o que é isso?","images":["data:image/jpeg;base64,AAAA"]}]""");
        Assert.Contains("imagem anexada", AssistantHistoryStore.Transcript(document.RootElement.EnumerateArray()));
    }

    [Fact]
    public void SearchResults_AreParsedFromTheResultsPage()
    {
        const string Html = """
            <div class="result"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Flaravel.com%2Fdocs%2F11.x%2Fqueues&amp;rut=abc">Queues | <b>Laravel</b> 11.x</a>
            <a class="result__snippet" href="//duckduckgo.com/l/?uddg=x">Filas &amp; <b>jobs</b> do Laravel.</a></div>
            <div class="result"><a rel="nofollow" class="result__a" href="https://duckduckgo.com/y.js?ad=1">Anúncio</a></div>
            <div class="result"><a rel="nofollow" class="result__a" href="https://exemplo.com/pagina">Sem trecho</a></div>
            """;

        var results = WebTools.ParseResults(Html);

        Assert.Equal(2, results.Count);
        Assert.Equal(new WebTools.SearchResult("Queues | Laravel 11.x", "https://laravel.com/docs/11.x/queues", "Filas & jobs do Laravel."), results[0]);
        Assert.Equal(new WebTools.SearchResult("Sem trecho", "https://exemplo.com/pagina", ""), results[1]);
    }

    [Fact]
    public void HtmlToText_KeepsHeadingsListsLinksAndCodeIndentation()
    {
        const string Html = """
            <html><head><title>Guia &amp; Exemplo</title><style>p{color:red}</style></head><body>
            <nav><a href="/menu">Menu</a></nav><script>alert(1)</script>
            <h2>Instalar</h2><p>Rode   o <a href="/docs/cli">comando</a> abaixo:</p>
            <pre><code>def f():
                return 1 &lt; 2</code></pre>
            <ul><li>Um</li><li>Dois</li></ul>
            </body></html>
            """;

        var text = WebTools.HtmlToText(Html, new Uri("https://exemplo.com/guia"));

        Assert.StartsWith("Guia & Exemplo\nhttps://exemplo.com/guia", text);
        Assert.Contains("# Instalar", text);
        Assert.Contains("Rode o [comando](https://exemplo.com/docs/cli) abaixo:", text);
        Assert.Contains("```\ndef f():\n    return 1 < 2\n```", text);
        Assert.Contains("- Um\n- Dois", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("Menu", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700::1111", false)]
    public void PrivateAddresses_AreRecognized(string address, bool expected) =>
        Assert.Equal(expected, WebTools.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public async Task WebFetch_RefusesInternalAddressesAndOtherSchemes()
    {
        var web = new WebTools(new HttpClient(new Pages()));
        await Assert.ThrowsAsync<IOException>(() => web.FetchAsync("http://127.0.0.1:8080/admin", 0, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => web.FetchAsync("http://192.168.1.1/", 0, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => web.FetchAsync("file:///etc/passwd", 0, CancellationToken.None));
    }

    [Fact]
    public async Task WebFetch_FollowsRedirectsChecksEachHopAndPagesLongText()
    {
        var pages = new Pages();
        var web = new WebTools(new HttpClient(pages));

        // 93.184.216.34 é um endereço público qualquer; nada sai para a rede (o manipulador responde).
        var text = await web.FetchAsync("http://93.184.216.34/inicio", 0, CancellationToken.None);
        Assert.Contains("[redirecionado para http://93.184.216.34/longa]", text);
        Assert.Contains("continue com offset 30000", text);

        var rest = await web.FetchAsync("http://93.184.216.34/longa", 30000, CancellationToken.None);
        Assert.EndsWith("fim", rest);

        // Redirecionar para a rede interna não passa.
        await Assert.ThrowsAsync<IOException>(() => web.FetchAsync("http://93.184.216.34/interno", 0, CancellationToken.None));
        Assert.DoesNotContain(pages.Requested, u => u.Host == "10.0.0.5");
    }

    [Fact]
    public async Task WebFetch_AsksInAskModeAndSearchDoesNot()
    {
        var fetch = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.WebFetch)!, default, AssistantMode.Ask);
        var search = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.WebSearch)!, default, AssistantMode.Ask);
        var auto = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.WebFetch)!, default, AssistantMode.Auto);
        Assert.Equal((PermissionKind.Ask, PermissionKind.Allow, PermissionKind.Allow), (fetch.Kind, search.Kind, auto.Kind));

        var none = await new AssistantTools(new Host()).ExecuteAsync(Call(AssistantTools.WebSearch, new { query = "x" }), CancellationToken.None);
        Assert.True(none.IsError);
    }

    [Fact]
    public async Task OpenAiModel_SendsAttachedImagesAsDataUrls()
    {
        var handler = new Capture("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var model = new OpenAiChatModel(new HttpClient(handler), "https://api.exemplo.com/v1", "k", "m");

        await model.CompleteAsync("s", [ChatTurn.FromUser("veja", [new ChatImage("image/jpeg", "QUJD")])], [], _ => { }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("veja", content[0].GetProperty("text").GetString());
        Assert.Equal("data:image/jpeg;base64,QUJD", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    /// <summary>Busca e leitura de verdade, na internet. Só roda com ROOTDOCK_WEB_TEST=1.</summary>
    [Fact]
    public async Task Web_SearchAndFetch_Live()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ROOTDOCK_WEB_TEST") == "1", "Defina ROOTDOCK_WEB_TEST=1 para testar na internet.");
        var web = new WebTools(WebTools.CreateClient());

        var results = await web.SearchAsync("xterm.js fit addon", TestContext.Current.CancellationToken);
        Assert.Contains("http", results);
        TestContext.Current.SendDiagnosticMessage(results);

        var page = await web.FetchAsync("https://example.com/", 0, TestContext.Current.CancellationToken);
        Assert.Contains("Example Domain", page);
    }

    private sealed class Capture(string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class Pages : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            HttpResponseMessage response;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/inicio":
                    response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("/longa", UriKind.Relative);
                    break;

                case "/interno":
                    response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                    response.Headers.Location = new Uri("http://10.0.0.5/segredo");
                    break;

                default:
                    response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(new string('a', 40_000) + "fim", Encoding.UTF8, "text/plain"),
                    };
                    break;
            }

            return Task.FromResult(response);
        }
    }

    private sealed class Host : IAssistantHost
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<(string Path, byte[]? Before)> Changes { get; } = [];

        public IReadOnlyList<AssistantTask> Tasks { get; private set; } = [];

        public string Label => "root@teste";

        public string? TerminalDirectory => null;

        public Task<CommandResult> RunAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct) => Task.FromResult(new CommandResult(0, "", ""));

        public Task<CommandResult> QueryAsync(string command, TimeSpan timeout, CancellationToken ct) => Task.FromResult(new CommandResult(0, "", ""));

        public void Remember(string note)
        {
        }

        public string ReadTerminal(int lines) => "";

        public void TypeInTerminal(string text)
        {
        }

        public Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken ct) =>
            Files.TryGetValue(path, out var bytes) ? Task.FromResult(bytes) : throw new IOException("Arquivo não encontrado: " + path);

        public Task WriteFileAsync(string path, byte[] content, CancellationToken ct)
        {
            Files[path] = content;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(Files.ContainsKey(path));

        public Task<IReadOnlyList<AssistantFileEntry>> ListAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AssistantFileEntry>>([]);

        public void BeforeChange(string path, byte[]? previous) => Changes.Add((path, previous));

        public void ShowTasks(IReadOnlyList<AssistantTask> tasks) => Tasks = tasks;
    }
}
