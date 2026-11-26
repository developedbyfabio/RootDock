using RootDock.Core.Assistant;
using Xunit;

namespace RootDock.Core.Tests;

public class CodexSessionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rootdock-codex-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("simples", "\"simples\"")]
    [InlineData("com \"aspas\" e \\barra", "\"com \\\"aspas\\\" e \\\\barra\"")]
    [InlineData("linha 1\r\nlinha 2\tfim", "\"linha 1\\nlinha 2\\tfim\"")]
    [InlineData("ação é ü", "\"ação é ü\"")]
    [InlineData("sino\u0007", "\"sino\\u0007\"")]
    public void Toml_WritesAOneLineBasicString(string value, string expected) =>
        Assert.Equal(expected, CodexSession.Toml(value));

    [Fact]
    public void BuildArguments_FirstMessage_TurnsLocalToolsOffAndPlugsRootDockIn()
    {
        var arguments = CodexSession.BuildArguments(null, " gpt-6-luna ", "high", "http://127.0.0.1:5000/mcp", "Regra 1.\nRegra \"2\".", ["C:\\a b\\i.jpg"]);
        Assert.Equal("exec", arguments[0]);
        Assert.DoesNotContain("resume", arguments);
        Assert.Contains("--json", arguments);
        Assert.Contains("--ignore-user-config", arguments);
        Assert.Contains("--ignore-rules", arguments);
        Assert.Equal("read-only", arguments[arguments.IndexOf("--sandbox") + 1]);
        foreach (var feature in (string[])["shell_tool", "unified_exec", "computer_use", "browser_use", "apps", "plugins", "hooks", "view_image"])
        {
            Assert.Contains(feature, CodexSession.DisabledFeatures);
            Assert.Equal("--disable", arguments[arguments.IndexOf(feature) - 1]);
        }

        Assert.Contains("mcp_servers.rootdock.url=\"http://127.0.0.1:5000/mcp\"", arguments);
        Assert.Contains("mcp_servers.rootdock.bearer_token_env_var=\"ROOTDOCK_MCP_TOKEN\"", arguments);
        Assert.Contains("mcp_servers.rootdock.default_tools_approval_mode=\"approve\"", arguments);
        Assert.Contains("developer_instructions=\"Regra 1.\\nRegra \\\"2\\\".\"", arguments);
        Assert.Equal("gpt-6-luna", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Contains("model_reasoning_effort=\"high\"", arguments);
        Assert.Equal("C:\\a b\\i.jpg", arguments[arguments.IndexOf("--image") + 1]);
        // A senha do MCP nunca vai na linha de comando; nenhum argumento tem quebra de linha.
        Assert.DoesNotContain(arguments, a => a.Contains('\n'));
    }

    [Fact]
    public void BuildArguments_NextMessages_ResumeTheSameConversation()
    {
        var arguments = CodexSession.BuildArguments("01a2-thread", null, "", "http://127.0.0.1:5000/mcp", "", []);
        Assert.Equal(["exec", "resume", "01a2-thread"], arguments.Take(3));
        Assert.DoesNotContain("--sandbox", arguments);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain(arguments, a => a.StartsWith("developer_instructions", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
        Assert.Contains("shell_tool", arguments); // continua desligado em toda rodada
    }

    [Fact]
    public void SplitInstructions_KeepsWholeParagraphs()
    {
        Assert.Equal(("curto", ""), CodexSession.SplitInstructions("curto", 100));
        Assert.Equal(("", "tudo vai junto da pergunta"), CodexSession.SplitInstructions("tudo vai junto da pergunta", 0));

        var system = new string('a', 60) + "\n\n" + new string('b', 60) + "\n\n" + new string('c', 60);
        var (inline, overflow) = CodexSession.SplitInstructions(system, 130);
        Assert.Equal(new string('a', 60) + "\n\n" + new string('b', 60), inline);
        Assert.Equal(new string('c', 60), overflow);
    }

    [Fact]
    public void ParseModels_ListsOnlyTheSelectableOnes_InPriorityOrder()
    {
        const string json = """
            {"models":[
             {"slug":"oculto","display_name":"Oculto","visibility":"hide","priority":1,"supported_reasoning_levels":[{"effort":"low"}]},
             {"slug":"b-modelo","display_name":"Modelo B","visibility":"list","priority":4,"supported_reasoning_levels":[{"effort":"low"},{"effort":"ultra"}]},
             {"slug":"a-modelo","display_name":"Modelo A","visibility":"list","priority":2,"supported_reasoning_levels":[{"effort":"medium","description":"x"}]},
             {"slug":"a-modelo","display_name":"repetido","visibility":"list","priority":9},
             {"display_name":"sem slug","visibility":"list"}]}
            """;
        var models = CodexSession.ParseModels(json)!;
        Assert.Equal(["a-modelo", "b-modelo"], models.Select(m => m.Value));
        Assert.Equal("Modelo A", models[0].Label);
        Assert.Equal(["medium"], models[0].Efforts);
        Assert.Equal(["low", "ultra"], models[1].Efforts);
        Assert.Null(CodexSession.ParseModels("não é json"));
        Assert.Null(CodexSession.ParseModels("""{"outra":"coisa"}"""));
    }

    [Theory]
    [InlineData("Logged in using ChatGPT\n", 0, true, "conta ChatGPT")]
    [InlineData("Logged in using an API key - sk-***\n", 0, true, "chave de API")]
    [InlineData("Not logged in\n", 1, false, null)]
    [InlineData("Not logged in\n", 0, false, null)]
    [InlineData("erro qualquer", 2, false, null)]
    public void ParseStatus_ReadsWhatCodexSays(string text, int exitCode, bool logged, string? account)
    {
        var status = CodexSession.ParseStatus(@"C:\x\codex.exe", text, exitCode);
        Assert.True(status.Installed);
        Assert.Equal(logged, status.LoggedIn);
        Assert.Equal(account, status.Account);
    }

    // Os textos são os que o codex.exe traz dentro dele.
    [Theory]
    [InlineData("You've hit your usage limit. Upgrade to Plus to continue using Codex (https://chatgpt.com/explore/plus), or try again at 3:15 PM.", "limite de uso")]
    [InlineData("You've reached your usage limit. Increase your limits to continue using codex.", "limite de uso")]
    [InlineData("You've reached your workspace credit limit", "limite de uso")]
    [InlineData("Your workspace is out of credits. Ask your workspace owner to add more.", "limite de uso")]
    [InlineData("Quota exceeded. Check your plan.", "limite de uso")]
    [InlineData("Codex is currently experiencing high load.", "sobrecarregado")]
    [InlineData("unexpected status 401 Unauthorized", "pediu login")]
    [InlineData("Not logged in. Run codex login.", "pediu login")]
    public void Friendly_ExplainsWhatThePersonCanDo(string error, string expected)
    {
        var text = CodexSession.Friendly(error)!;
        Assert.Contains(expected, text);
        Assert.Contains(error, text); // o texto original segue junto
    }

    [Theory]
    [InlineData("The 'x' model is not supported when using Codex with a ChatGPT account.")]
    [InlineData("stream disconnected before completion")]
    public void Friendly_LeavesOtherErrorsAsTheyCame(string error) => Assert.Null(CodexSession.Friendly(error));

    [Fact]
    public void FirstUsable_SkipsWhatDoesNotExistAndStoreStubs_AndIsNullWithoutCodex()
    {
        Directory.CreateDirectory(_folder);
        var stub = Path.Combine(_folder, "atalho-da-loja.exe");
        File.WriteAllBytes(stub, []);
        var real = Path.Combine(_folder, "codex.exe");
        File.WriteAllText(real, "x");
        var cmd = Path.Combine(_folder, "codex.cmd");
        File.WriteAllText(cmd, "");

        Assert.Null(CodexSession.FirstUsable([]));
        Assert.Null(CodexSession.FirstUsable([Path.Combine(_folder, "nao-existe.exe"), stub, "caminho\0inválido"]));
        Assert.Equal(real, CodexSession.FirstUsable([Path.Combine(_folder, "nao-existe.exe"), stub, real, cmd]));
        Assert.Equal(cmd, CodexSession.FirstUsable([stub, cmd])); // atalho .cmd do npm vale mesmo vazio
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData(" OFF ", true)]
    [InlineData("\"none\"", true)]
    [InlineData(@"C:\none\codex.exe", false)]
    [InlineData("", false)]
    public void IsOff_OnlyTheSwitchWords(string value, bool off) => Assert.Equal(off, ClaudeCodeSession.IsOff(value));

    [Fact]
    public void AppCopies_PrefersTheNewestCopyTheAppKeeps()
    {
        foreach (var (name, age) in new[] { ("aaaa", 30), ("bbbb", 1), ("cccc", 10) })
        {
            var folder = Path.Combine(_folder, name);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "codex.exe");
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-age));
        }

        Directory.CreateDirectory(Path.Combine(_folder, "sem-codex"));
        var copies = CodexSession.AppCopies(_folder).ToList();
        Assert.Equal(["bbbb", "cccc", "aaaa"], copies.Select(c => Path.GetFileName(Path.GetDirectoryName(c))));
        Assert.Empty(CodexSession.AppCopies(Path.Combine(_folder, "nao-existe")));
    }
}
