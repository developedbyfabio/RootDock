using System.Text;
using System.Text.Json;
using RootDock.Core.Assistant;
using RootDock.Core.MultiExec;

namespace RootDock.Core.Tests;

public sealed class AssistantTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolCall Call(string name, object input) => new("c1", name, Json(input));

    // ---------- Permissões ----------

    [Theory]
    [InlineData(AssistantTools.ReadFile)]
    [InlineData(AssistantTools.ListDirectory)]
    [InlineData(AssistantTools.ReadTerminalTool)]
    public void ReadOnlyTools_NeverAsk(string tool)
    {
        var check = AssistantPolicy.Evaluate(AssistantTools.Find(tool)!, Json(new { path = "/etc/hosts" }), AssistantMode.Ask);
        Assert.Equal(PermissionKind.Allow, check.Kind);
    }

    [Fact]
    public void AskMode_AsksForCommandsAndWrites()
    {
        Assert.Equal(PermissionKind.Ask, AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.RunCommand)!, Json(new { command = "ls -la" }), AssistantMode.Ask).Kind);
        Assert.Equal(PermissionKind.Ask, AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.WriteFile)!, Json(new { path = "/tmp/a", content = "x" }), AssistantMode.Ask).Kind);
    }

    [Fact]
    public void AutoMode_RunsOrdinaryCommandsAlone()
    {
        var check = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.RunCommand)!, Json(new { command = "systemctl restart nginx" }), AssistantMode.Auto);
        Assert.Equal(PermissionKind.Allow, check.Kind);
    }

    [Theory]
    [InlineData("mysql -e 'DROP TABLE pedidos'")]
    [InlineData("mysql -e \"DELETE FROM clientes\"")]
    [InlineData("mysql -e \"TRUNCATE TABLE logs\"")]
    [InlineData("git push --force origin main")]
    [InlineData("git push origin --delete feature")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git clean -fd")]
    [InlineData("git stash clear")]
    [InlineData("php artisan migrate:fresh")]
    [InlineData("php artisan db:wipe")]
    // Variações de escrita que não podem escapar.
    [InlineData("mysql -e \"DELETE FROM \\`pedidos\\`\"")]
    [InlineData("psql -c \"DELETE FROM \\\"Users\\\"\"")]
    [InlineData("mysql -e \"delete\n  from $tabela\"")]
    [InlineData("sqlcmd -Q \"DELETE FROM [dbo].[x]\"")]
    [InlineData("mysql -e \"UPDATE \\`t\\` SET a=1\"")]
    [InlineData("mysql -e \"TRUNCATE \\`t\\`\"")]
    [InlineData("dropdb loja")]
    [InlineData("mysqladmin -u root drop loja")]
    [InlineData("mysql -e 'ALTER TABLE t DROP coluna'")]
    [InlineData("psql -c 'DROP ROLE app'")]
    [InlineData("git push -uf origin main")]
    [InlineData("git push -fu origin main")]
    [InlineData("git push \\\n  --force origin main")]
    [InlineData("git -C /srv/app push -f")]
    [InlineData("git push origin +main")]
    [InlineData("git push origin :feature")]
    [InlineData("git checkout .")]
    [InlineData("git checkout HEAD -- .")]
    [InlineData("git checkout -- ./")]
    [InlineData("git restore -- .")]
    [InlineData("git restore -s HEAD .")]
    [InlineData("git restore ./")]
    [InlineData("git clean -d -f")]
    [InlineData("git clean --force")]
    [InlineData("cd /srv/app && git stash drop")]
    public void OrganizationList_AlwaysNeedsAPerson_EvenInAuto(string command)
    {
        foreach (var tool in new[] { AssistantTools.RunCommand, AssistantTools.TypeInTerminal })
        {
            var input = tool == AssistantTools.RunCommand ? Json(new { command }) : Json(new { text = command });
            var check = AssistantPolicy.Evaluate(AssistantTools.Find(tool)!, input, AssistantMode.Auto);
            Assert.Equal(PermissionKind.AskDestructive, check.Kind);
            Assert.NotEmpty(check.Reasons);
        }
    }

    [Theory]
    [InlineData("rm -rf /var/www/app")]
    [InlineData("rm /root/teste")]
    [InlineData("sudo reboot")]
    [InlineData("systemctl stop nginx")]
    [InlineData("apt-get -y purge nginx")]
    [InlineData("docker system prune -af")]
    [InlineData("echo rm teste")]
    [InlineData("git push origin main")]
    [InlineData("git reset --soft HEAD~1")]
    [InlineData("mysql -e \"UPDATE clientes SET ativo=0 WHERE id=7\"")]
    [InlineData("mysqldump loja > /root/loja.sql")]
    [InlineData("git push -u origin main")]
    [InlineData("git push --follow-tags")]
    [InlineData("git checkout main")]
    [InlineData("git checkout -b nova")]
    [InlineData("git checkout -- src/app.php")]
    [InlineData("git checkout ../outro")]
    [InlineData("git restore src/app.php")]
    [InlineData("git clean -n")]
    [InlineData("git stash")]
    [InlineData("git stash pop")]
    [InlineData("truncate -s 0 /var/log/app.log")]
    [InlineData("mysql -e \"UPDATE \\`t\\` SET a=1 WHERE id=2\"")]
    [InlineData("grep -r dropdown resources/")]
    [InlineData("ls -la . && git status")]
    public void AutoMode_HasFullFreedom_OutsideTheOrganizationList(string command)
    {
        var check = AssistantPolicy.Evaluate(AssistantTools.Find(AssistantTools.RunCommand)!, Json(new { command }), AssistantMode.Auto);
        Assert.Equal(PermissionKind.Allow, check.Kind);
    }

    // ---------- Ferramentas ----------

    [Fact]
    public async Task RunCommand_UsesTerminalDirectory_AndReportsExitCode()
    {
        var host = new FakeHost { Result = new CommandResult(3, "saida\n", "aviso\n") };
        var result = await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.RunCommand, new { command = "git status", cwd = "/srv/www/app" }), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("git status", host.Commands.Single());
        Assert.Equal("/srv/www/app", host.Directories.Single());
        Assert.Equal("cd '/srv/www/app' && { git status\n}", AssistantTools.ForChannel("git status", "/srv/www/app"));
        Assert.Equal("git status", AssistantTools.ForChannel("git status", null));
        Assert.Contains("saida", result.Content);
        Assert.Contains("[stderr]\naviso", result.Content);
        Assert.EndsWith("[código de retorno: 3]", result.Content);
    }

    [Fact]
    public async Task RunCommand_Timeout_ExplainsInsteadOfFailing()
    {
        var host = new FakeHost { Timeout = true };
        var result = await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.RunCommand, new { command = "top", timeout_seconds = 5 }), TestContext.Current.CancellationToken);
        Assert.Contains("não terminou em 5 s", result.Content);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task RunCommand_WithoutTimeout_RunsUntilTheEnd()
    {
        var host = new FakeHost();
        await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.RunCommand, new { command = "du -h -x -d 1 /" }), TestContext.Current.CancellationToken);
        Assert.True(host.Timeouts.Single() >= TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task RunCommand_LongOutput_KeepsHeadAndTail()
    {
        var host = new FakeHost { Result = new CommandResult(0, "INICIO" + new string('x', 200_000) + "FIM", "") };
        var result = await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.RunCommand, new { command = "cat big" }), TestContext.Current.CancellationToken);
        Assert.Contains("INICIO", result.Content);
        Assert.Contains("FIM", result.Content);
        Assert.Contains("caracteres omitidos", result.Content);
        Assert.True(result.Content.Length < 40_000);
    }

    [Fact]
    public async Task ReadFile_NumbersLines_AndPages()
    {
        var host = new FakeHost();
        host.Files["/etc/app.conf"] = Encoding.UTF8.GetBytes("a\nb\nc\nd\n");
        var tools = new AssistantTools(host);
        var all = await tools.ExecuteAsync(Call(AssistantTools.ReadFile, new { path = "/etc/app.conf" }), TestContext.Current.CancellationToken);
        Assert.StartsWith("1\ta\n2\tb\n", all.Content);
        var page = await tools.ExecuteAsync(Call(AssistantTools.ReadFile, new { path = "/etc/app.conf", offset = 2, limit = 2 }), TestContext.Current.CancellationToken);
        Assert.StartsWith("2\tb\n3\tc\n", page.Content);
        Assert.Contains("continue com offset 4", page.Content);
    }

    [Fact]
    public async Task ReadFile_RelativePath_IsRefused()
    {
        var result = await new AssistantTools(new FakeHost()).ExecuteAsync(Call(AssistantTools.ReadFile, new { path = "app.conf" }), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("absoluto", result.Content);
    }

    [Fact]
    public async Task EditFile_KeepsCrLfAndLatin1()
    {
        var host = new FakeHost();
        host.Files["/srv/a.ini"] = Encoding.Latin1.GetBytes("nome=João\r\nporta=80\r\n");
        var result = await new AssistantTools(host).ExecuteAsync(
            Call(AssistantTools.EditFile, new { path = "/srv/a.ini", old_text = "porta=80", new_text = "porta=8080" }), TestContext.Current.CancellationToken);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("nome=João\r\nporta=8080\r\n", Encoding.Latin1.GetString(host.Files["/srv/a.ini"]));
    }

    [Fact]
    public async Task EditFile_AmbiguousOrMissing_DoesNotWrite()
    {
        var host = new FakeHost();
        host.Files["/srv/a.txt"] = Encoding.UTF8.GetBytes("x\nx\n");
        var tools = new AssistantTools(host);
        var twice = await tools.ExecuteAsync(Call(AssistantTools.EditFile, new { path = "/srv/a.txt", old_text = "x", new_text = "y" }), TestContext.Current.CancellationToken);
        Assert.True(twice.IsError);
        Assert.Contains("2 vezes", twice.Content);
        var missing = await tools.ExecuteAsync(Call(AssistantTools.EditFile, new { path = "/srv/a.txt", old_text = "z", new_text = "y" }), TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
        Assert.Equal("x\nx\n", Encoding.UTF8.GetString(host.Files["/srv/a.txt"]));
        var all = await tools.ExecuteAsync(Call(AssistantTools.EditFile, new { path = "/srv/a.txt", old_text = "x", new_text = "y", replace_all = true }), TestContext.Current.CancellationToken);
        Assert.False(all.IsError);
        Assert.Equal("y\ny\n", Encoding.UTF8.GetString(host.Files["/srv/a.txt"]));
    }

    [Fact]
    public async Task WriteFile_CreatesUtf8WithoutBom()
    {
        var host = new FakeHost();
        var result = await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.WriteFile, new { path = "/tmp/novo.sh", content = "#!/bin/sh\r\necho ção\n" }), TestContext.Current.CancellationToken);
        Assert.Contains("criado", result.Content);
        Assert.Equal("#!/bin/sh\necho ção\n", Encoding.UTF8.GetString(host.Files["/tmp/novo.sh"]));
    }

    [Fact]
    public async Task TypeInTerminal_SendsEnterByDefault()
    {
        var host = new FakeHost();
        await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.TypeInTerminal, new { text = "ls\npwd" }), TestContext.Current.CancellationToken);
        Assert.Equal("ls\rpwd\r", host.Typed.Single());
    }

    [Fact]
    public async Task SearchAndFind_AreReadOnly_SkipDependencies_AndCapResults()
    {
        var host = new FakeHost { QueryOutput = string.Join('\n', Enumerable.Range(1, 250).Select(i => $"/srv/app/a.php:{i}:linha")) };
        var tools = new AssistantTools(host);
        Assert.True(AssistantTools.Find(AssistantTools.SearchFiles)!.ReadOnly);
        Assert.True(AssistantTools.Find(AssistantTools.FindFiles)!.ReadOnly);

        var search = await tools.ExecuteAsync(Call(AssistantTools.SearchFiles, new { pattern = "it's", path = "/srv/app", glob = "*.php", ignore_case = true }), TestContext.Current.CancellationToken);
        Assert.Contains("grep -rnIi -E --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=vendor", host.Queries[0]);
        Assert.Contains("--include='*.php' -e 'it'\\''s' -- '/srv/app'", host.Queries[0]);
        Assert.Contains("/srv/app/a.php:200:linha", search.Content);
        Assert.DoesNotContain("/srv/app/a.php:201:linha", search.Content);
        Assert.Contains("primeiras 200", search.Content);
        Assert.Empty(host.Commands); // não passa pelo terminal nem pede confirmação

        host.QueryOutput = "";
        var find = await tools.ExecuteAsync(Call(AssistantTools.FindFiles, new { pattern = "*Controller.php", path = "/srv/app" }), TestContext.Current.CancellationToken);
        Assert.Contains("-prune -o -type f -name '*Controller.php' -print", host.Queries[1]);
        Assert.Equal("Nenhum arquivo com esse nome.", find.Content);
    }

    [Fact]
    public async Task ReadFile_TooLargeForSftp_ReadsTheAskedLinesOnTheServer()
    {
        var host = new FakeHost { QueryOutput = "500\tlinha quinhentos\n" };
        var result = await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.ReadFile, new { path = "/var/log/enorme.log", offset = 500, limit = 100 }), TestContext.Current.CancellationToken);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("NR>=500 && NR<=599", host.Queries.Single());
        Assert.Contains("500\tlinha quinhentos", result.Content);
        Assert.Contains("offset 600", result.Content);
    }

    [Fact]
    public async Task Remember_SavesTheNote()
    {
        var host = new FakeHost();
        await new AssistantTools(host).ExecuteAsync(Call(AssistantTools.RememberTool, new { note = "O projeto fica em /srv/www/hub" }), TestContext.Current.CancellationToken);
        Assert.Equal(["O projeto fica em /srv/www/hub"], host.Notes);
    }

    [Fact]
    public void MemoryStore_AppendsNotes_PerServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-mem-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AssistantMemoryStore(directory);
            Assert.Equal("", store.Read("10.0.0.1:22"));
            store.Append("10.0.0.1:22", "Projeto em /srv/www");
            store.Append("10.0.0.1:22", "Deploy com\ngit pull");
            Assert.Equal("- Projeto em /srv/www\n- Deploy com git pull\n", store.Read("10.0.0.1:22"));
            Assert.Equal("", store.Read("10.0.0.2:22"));
            store.Write("10.0.0.1:22", "texto novo");
            Assert.Equal("texto novo\n", store.Read("10.0.0.1:22"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- Laço da conversa ----------

    [Fact]
    public async Task Agent_LongConversation_IsSummarized_AndTheWorkContinues()
    {
        var host = new FakeHost { Result = new CommandResult(0, new string('x', 3000), "") };
        var model = new ScriptedModel(
            new ChatTurn { Role = ChatRole.Assistant, Calls = [Call(AssistantTools.RunCommand, new { command = "cat grande" })] },
            new ChatTurn { Role = ChatRole.Assistant, Text = "RESUMO: a pessoa pediu X; rodei cat grande." },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Terminei." });
        var observer = new RecordingObserver();
        var runner = new AssistantToolRunner(new AssistantTools(host), observer) { Mode = AssistantMode.Auto };
        var agent = new AssistantAgent(model, runner, observer) { CompactAtChars = 1000 };

        await agent.SendAsync("sistema", "faça X", TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Requests.Count);
        // 2º pedido: o resumo, sem ferramentas, com a conversa em texto.
        Assert.Contains("PESSOA: faça X", model.Requests[1].Single().Text);
        Assert.Contains("FERRAMENTA run_command", model.Requests[1].Single().Text);
        // 3º pedido: a conversa agora é só o resumo, e o trabalho segue.
        var compacted = model.Requests[2].Single();
        Assert.Equal(ChatRole.User, compacted.Role);
        Assert.Contains("RESUMO: a pessoa pediu X", compacted.Text);
        Assert.Contains("Continue o trabalho", compacted.Text);
        Assert.Contains(observer.Notes, n => n.Contains("resumi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Agent_RunsTools_AndFeedsResultsBack()
    {
        var host = new FakeHost { Result = new CommandResult(0, "up 3 days\n", "") };
        var model = new ScriptedModel(
            new ChatTurn { Role = ChatRole.Assistant, Text = "Vou ver.", Calls = [Call(AssistantTools.RunCommand, new { command = "uptime" })] },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Está no ar há 3 dias." });
        var observer = new RecordingObserver { Answer = true };
        var runner = new AssistantToolRunner(new AssistantTools(host), observer) { Mode = AssistantMode.Auto };
        await new AssistantAgent(model, runner, observer).SendAsync("sistema", "há quanto tempo está no ar?", TestContext.Current.CancellationToken);

        Assert.Equal(["uptime"], host.Commands);
        Assert.Equal(0, observer.Confirmations);
        Assert.Equal(2, model.Requests.Count);
        var fed = model.Requests[1];
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], fed.Select(t => t.Role));
        Assert.Contains("up 3 days", fed[2].Results.Single().Content);
    }

    [Fact]
    public async Task Agent_DeniedDestructiveCommand_DoesNotRun_AndTellsTheModel()
    {
        var host = new FakeHost();
        var model = new ScriptedModel(
            new ChatTurn { Role = ChatRole.Assistant, Calls = [Call(AssistantTools.RunCommand, new { command = "git reset --hard HEAD~3" })] },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Certo, não apaguei." });
        var observer = new RecordingObserver { Answer = false };
        var runner = new AssistantToolRunner(new AssistantTools(host), observer) { Mode = AssistantMode.Auto };
        await new AssistantAgent(model, runner, observer).SendAsync("sistema", "limpa a pasta", TestContext.Current.CancellationToken);

        Assert.Empty(host.Commands);
        Assert.Equal(1, observer.Confirmations);
        Assert.Equal(PermissionKind.AskDestructive, observer.LastCheck!.Kind);
        var result = model.Requests[1][2].Results.Single();
        Assert.True(result.IsError);
        Assert.Contains("não autorizou", result.Content);
    }

    [Fact]
    public async Task Agent_FirstRequestFails_QuestionLeavesHistory()
    {
        var observer = new RecordingObserver();
        var runner = new AssistantToolRunner(new AssistantTools(new FakeHost()), observer);
        var agent = new AssistantAgent(new ScriptedModel(), runner, observer);
        await Assert.ThrowsAsync<ChatModelException>(() => agent.SendAsync("sistema", "oi", TestContext.Current.CancellationToken));
        Assert.False(agent.HasHistory);
    }

    // ---------- Servidor MCP (o caminho do Claude Code) ----------

    [Fact]
    public async Task McpServer_RequiresToken_ListsAndCallsTools()
    {
        var host = new FakeHost { Result = new CommandResult(0, "oi\n", "") };
        var observer = new RecordingObserver();
        var runner = new AssistantToolRunner(new AssistantTools(host), observer) { Mode = AssistantMode.Auto };
        using var server = new McpToolServer(runner.RunAsync);
        using var http = new HttpClient();

        async Task<(int Status, JsonElement Body)> Post(object body, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return ((int)response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement : default);
        }

        Assert.Equal(401, (await Post(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, null)).Status);
        Assert.Equal(401, (await Post(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, "errado")).Status);

        var init = await Post(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }, server.Token);
        Assert.Equal("2025-06-18", init.Body.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(202, (await Post(new { jsonrpc = "2.0", method = "notifications/initialized" }, server.Token)).Status);

        var list = await Post(new { jsonrpc = "2.0", id = 2, method = "tools/list" }, server.Token);
        Assert.Equal(AssistantTools.Definitions.Count, list.Body.GetProperty("result").GetProperty("tools").GetArrayLength());

        var call = await Post(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = AssistantTools.RunCommand, arguments = new { command = "echo oi" } } }, server.Token);
        var result = call.Body.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Contains("oi", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(["echo oi"], host.Commands);
    }

    /// <summary>
    /// Conversa de verdade com o Claude Code instalado (gasta um pouco da conta logada):
    /// só roda com ROOTDOCK_CLAUDE_TEST=1.
    /// </summary>
    [Fact]
    public async Task ClaudeCode_UsesRootDockTools_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("ROOTDOCK_CLAUDE_TEST") != "1" || ClaudeCodeSession.Find() is not { } claude)
        {
            Assert.Skip("Defina ROOTDOCK_CLAUDE_TEST=1 com o Claude Code instalado e logado.");
            return;
        }

        var host = new FakeHost { Result = new CommandResult(0, "rootdock-teste-7391\n", "") };
        var observer = new RecordingObserver { Answer = true };
        var runner = new AssistantToolRunner(new AssistantTools(host), observer) { Mode = AssistantMode.Auto };
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-claude-test-" + Guid.NewGuid().ToString("N")[..8]);
        using var session = new ClaudeCodeSession(claude, directory, "haiku", runner, observer);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        await session.SendAsync(
            "Você age num servidor Linux pelas ferramentas rootdock. Responda em português.",
            "Rode o comando `hostname` com a ferramenta run_command e me diga exatamente o que ele imprimiu.", limit.Token);
        Assert.Contains(host.Commands, c => c.Contains("hostname", StringComparison.Ordinal));
        Assert.Contains("rootdock-teste-7391", observer.Output.ToString());

        // Segunda mensagem na mesma conversa: ele lembra da primeira.
        observer.Output.Clear();
        await session.SendAsync("", "Repita só o texto que o comando imprimiu, sem rodar nada.", limit.Token);
        Assert.Contains("rootdock-teste-7391", observer.Output.ToString());
        Assert.Single(host.Commands);
    }

    // ---------- Provedores compatíveis com OpenAI (Gemini, OpenAI, Groq...) ----------

    [Fact]
    public async Task OpenAiModel_StreamsText_AndAssemblesToolCallsWithExtraFields()
    {
        var sse = string.Join("\n\n",
            """data: {"choices":[{"delta":{"role":"assistant","content":"Vou "}}]}""",
            """data: {"choices":[{"delta":{"content":"ver."}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","extra_content":{"google":{"thought_signature":"abc"}},"function":{"name":"run_command","arguments":"{\"comm"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"and\":\"uptime\"}"}}]}}]}""",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "data: [DONE]") + "\n\n";
        var handler = new StubHandler(sse);
        var model = new OpenAiChatModel(new HttpClient(handler), "https://exemplo.test/v1/", "chave", "modelo-x");
        var text = new StringBuilder();

        var turn = await model.CompleteAsync("sistema", [ChatTurn.FromUser("oi")], AssistantTools.Definitions, s => text.Append(s), TestContext.Current.CancellationToken);

        Assert.Equal("Vou ver.", text.ToString());
        var call = Assert.Single(turn.Calls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("uptime", call.Input.GetProperty("command").GetString());
        Assert.Equal("https://exemplo.test/v1/chat/completions", handler.Requests[0].Uri);
        Assert.Equal("Bearer chave", handler.Requests[0].Authorization);

        // Na volta, a chamada vai como o provedor mandou (com o campo extra) e o resultado como "tool".
        await model.CompleteAsync("sistema",
            [ChatTurn.FromUser("oi"), turn, ChatTurn.FromResults([new ToolResult("call_1", "up 3 days", false)])],
            AssistantTools.Definitions, _ => { }, TestContext.Current.CancellationToken);
        using var sent = JsonDocument.Parse(handler.Requests[1].Body);
        var messages = sent.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var echoed = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("abc", echoed.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
        Assert.Equal("{\"command\":\"uptime\"}", echoed.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(AssistantTools.Definitions.Count, sent.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task OpenAiModel_Overloaded_RetriesBeforeGivingUp()
    {
        var before = OpenAiChatModel.RetryDelays;
        OpenAiChatModel.RetryDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];
        try
        {
            var ok = "data: {\"choices\":[{\"delta\":{\"content\":\"oi\"}}]}\n\ndata: [DONE]\n\n";
            var handler = new StubHandler(ok) { FailFirst = 2 };
            var model = new OpenAiChatModel(new HttpClient(handler), "https://exemplo.test/v1", "k", "m");
            var turn = await model.CompleteAsync("s", [ChatTurn.FromUser("oi")], [], _ => { }, TestContext.Current.CancellationToken);
            Assert.Equal("oi", turn.Text);
            Assert.Equal(3, handler.Requests.Count);

            var down = new StubHandler(ok) { FailFirst = 99 };
            var error = await Assert.ThrowsAsync<ChatModelException>(() =>
                new OpenAiChatModel(new HttpClient(down), "https://exemplo.test/v1", "k", "m")
                    .CompleteAsync("s", [ChatTurn.FromUser("oi")], [], _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(4, down.Requests.Count);
            Assert.Contains("sobrecarregado", error.Message);
        }
        finally
        {
            OpenAiChatModel.RetryDelays = before;
        }
    }

    [Fact]
    public async Task OpenAiModel_BadKey_ExplainsInPortuguese()
    {
        var handler = new StubHandler("""{"error":{"message":"API key not valid"}}""", System.Net.HttpStatusCode.Unauthorized);
        var model = new OpenAiChatModel(new HttpClient(handler), "https://exemplo.test/v1", "x", "m");
        var error = await Assert.ThrowsAsync<ChatModelException>(() =>
            model.CompleteAsync("s", [ChatTurn.FromUser("oi")], [], _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("Chave de API recusada", error.Message);
        Assert.Contains("API key not valid", error.Message);
    }

    [Fact]
    public async Task OpenAiModel_ListsModels_WithoutGeminiPrefix()
    {
        var handler = new StubHandler("""{"data":[{"id":"models/gemini-b"},{"id":"gemini-a"}]}""");
        var models = await OpenAiChatModel.ListModelsAsync(new HttpClient(handler), "https://exemplo.test/v1", "k", TestContext.Current.CancellationToken);
        Assert.Equal(["gemini-a", "gemini-b"], models);
    }

    // ---------- Leitura do terminal ----------

    [Fact]
    public void TerminalText_StripsColors_AndAppliesLineRewrites()
    {
        var raw = "\u001b]0;titulo\u0007\u001b[01;32mroot@srv\u001b[0m:\u001b[01;34m/srv\u001b[0m# ls\r\n" +
                  "baixando 10%\rbaixando 100%\r\n" +
                  "abc\b\bXY\r\n" +
                  "linha comprida\r\u001b[Kcurta\r\n" +
                  "root@srv:/srv# ";
        Assert.Equal("root@srv:/srv# ls\nbaixando 100%\naXY\ncurta\nroot@srv:/srv#", TerminalText.Tail(raw, 50));
        Assert.Equal("curta\nroot@srv:/srv#", TerminalText.Tail(raw, 2));
    }

    // ---------- Histórico de conversas ----------

    [Fact]
    public void History_SavesPerServer_ListsNewestFirst_AndBuildsTranscript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rootdock-hist-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AssistantHistoryStore(directory);
            Assert.Empty(store.List("10.0.0.1:22"));
            JsonElement[] events =
            [
                Json(new { type = "user", text = "quanto espaço livre?" }),
                Json(new { type = "tool", id = "1", name = "run_command", summary = "df -h /" }),
                Json(new { type = "toolEnd", id = "1", ok = true, output = "12G livres" }),
                Json(new { type = "delta", text = "Existem 12 GB" }),
                Json(new { type = "delta", text = " livres." }),
                Json(new { type = "done" }),
            ];
            store.Save(new AssistantHistoryRecord { Id = "a1", Server = "10.0.0.1:22", Title = "antiga", Updated = DateTime.UtcNow.AddHours(-2), Events = [.. events] });
            store.Save(new AssistantHistoryRecord { Id = "b2", Server = "10.0.0.1:22", Title = "nova", Updated = DateTime.UtcNow, Events = [.. events] });
            store.Save(new AssistantHistoryRecord { Id = "c3", Server = "10.0.0.9:22", Title = "de outro servidor", Events = [.. events] });

            Assert.Equal(["nova", "antiga"], store.List("10.0.0.1:22").Select(i => i.Title));
            var loaded = store.Load("a1")!;
            Assert.Equal(6, loaded.Events.Count);
            Assert.Equal(
                "PESSOA: quanto espaço livre?\nFERRAMENTA run_command: df -h /\nRESULTADO: 12G livres\nASSISTENTE: Existem 12 GB livres.",
                AssistantHistoryStore.Transcript(loaded.Events));
            Assert.Null(store.Load("naoexiste"));
            Assert.Null(store.Load("..\\fora"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- Apoio ----------

    private sealed class FakeHost : IAssistantHost
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Commands { get; } = [];

        public List<string> Typed { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public List<string?> Directories { get; } = [];

        public CommandResult Result { get; set; } = new(0, "", "");

        public bool Timeout { get; set; }

        public string Label => "root@teste";

        public string? TerminalDirectory { get; set; }

        public Task<CommandResult> RunAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(command);
            Directories.Add(cwd);
            Timeouts.Add(timeout);
            return Timeout ? throw new TimeoutException() : Task.FromResult(Result);
        }

        public List<string> Queries { get; } = [];

        public List<string> Notes { get; } = [];

        public string QueryOutput { get; set; } = "";

        public Task<CommandResult> QueryAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            Queries.Add(command);
            return Task.FromResult(new CommandResult(0, QueryOutput, ""));
        }

        public void Remember(string note) => Notes.Add(note);

        public string ReadTerminal(int lines) => "$ ls\narquivo.txt";

        public void TypeInTerminal(string text) => Typed.Add(text);

        public Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken ct) =>
            path.EndsWith(".log", StringComparison.Ordinal) ? throw new IOException("Arquivo grande demais para o editor (900 MB).")
            : Files.TryGetValue(path, out var bytes) ? Task.FromResult(bytes) : throw new IOException("Arquivo não encontrado: " + path);

        public Task WriteFileAsync(string path, byte[] content, CancellationToken ct)
        {
            Files[path] = content;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(Files.ContainsKey(path));

        public Task<IReadOnlyList<AssistantFileEntry>> ListAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AssistantFileEntry>>([]);
    }

    private sealed class StubHandler(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        /// <summary>Quantos pedidos respondem 503 antes do corpo configurado.</summary>
        public int FailFirst { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Requests.Count <= FailFirst)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"high demand\"}}") };
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class ScriptedModel(params ChatTurn[] replies) : IChatModel
    {
        private int _next;

        public List<List<ChatTurn>> Requests { get; } = [];

        public Task<ChatTurn> CompleteAsync(
            string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolDefinition> tools, Action<string> onText, CancellationToken ct)
        {
            Requests.Add([.. history]);
            if (_next >= replies.Length)
            {
                throw new ChatModelException("sem resposta");
            }

            var reply = replies[_next++];
            onText(reply.Text);
            return Task.FromResult(reply);
        }
    }

    private sealed class RecordingObserver : IAssistantObserver
    {
        public bool Answer { get; set; }

        public int Confirmations { get; private set; }

        public PermissionCheck? LastCheck { get; private set; }

        public StringBuilder Output { get; } = new();

        public void Text(string delta)
        {
            lock (Output)
            {
                Output.Append(delta);
            }
        }

        public void ToolStarted(ToolCall call, string summary)
        {
        }

        public void ToolFinished(ToolCall call, ToolResult result)
        {
        }

        public Task<bool> ConfirmAsync(ToolCall call, string summary, PermissionCheck check, CancellationToken ct)
        {
            Confirmations++;
            LastCheck = check;
            return Task.FromResult(Answer);
        }

        public List<string> Notes { get; } = [];

        public void Note(string text) => Notes.Add(text);
    }
}
