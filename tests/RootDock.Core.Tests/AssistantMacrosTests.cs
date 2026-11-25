using System.Text.Json;
using RootDock.Core.Assistant;
using RootDock.Core.Macros;
using Xunit;

namespace RootDock.Core.Tests;

public class AssistantMacrosTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static object Health() => new
    {
        name = "Saúde do servidor",
        description = "Carga, memória e disco.",
        steps = new object[]
        {
            new { kind = "command", text = "echo \"== Carga ==\" && uptime" },
            new { kind = "wait", seconds = 1.5 },
            new { kind = "command", text = "sudo apt-get upgrade" },
            new { kind = "wait_for", text = "[Y/n]", seconds = 60 },
            new { kind = "command", text = "n" },
        },
    };

    [Fact]
    public void Build_TurnsTheRequestIntoAMacroTheRunnerUnderstands()
    {
        var macro = AssistantMacros.Build(Json(Health()), []);
        Assert.Equal("Saúde do servidor", macro.Name);
        Assert.Equal("Carga, memória e disco.", macro.Description);
        Assert.True(macro.WaitForPrompt);
        Assert.StartsWith("macro.", macro.Id);
        Assert.Equal(
            [MacroStepKind.Command, MacroStepKind.Wait, MacroStepKind.Command, MacroStepKind.WaitFor, MacroStepKind.Command],
            macro.Steps.Select(s => s.Kind));
        Assert.Equal(1.5, macro.Steps[1].Seconds);
        Assert.Equal("[Y/n]", macro.Steps[3].Text);
        Assert.Equal(60, macro.Steps[3].Seconds);
        Assert.Equal(5, MacroRunner.Runnable(macro).Count);
    }

    [Fact]
    public void Build_DefaultsKindToCommandAndExpectTimeout()
    {
        var macro = AssistantMacros.Build(Json(new
        {
            name = "x",
            wait_for_prompt = false,
            steps = new object[] { new { text = "passwd" }, new { kind = "wait_for", text = "password:" } },
        }), []);
        Assert.False(macro.WaitForPrompt);
        Assert.Equal(MacroStepKind.Command, macro.Steps[0].Kind);
        Assert.Equal(MacroRunner.DefaultExpectSeconds, macro.Steps[1].Seconds);
    }

    [Theory]
    [InlineData("""{"steps":[{"kind":"command","text":"ls"}]}""", "nome")]
    [InlineData("""{"name":"a","steps":[]}""", "passos")]
    [InlineData("""{"name":"a"}""", "passos")]
    [InlineData("""{"name":"a","steps":[{"kind":"command","text":"ls\npwd"}]}""", "uma linha só")]
    [InlineData("""{"name":"a","steps":[{"kind":"command","text":"ls\tx"}]}""", "caracteres de controle")]
    [InlineData("""{"name":"a","steps":[{"kind":"command","text":"  "}]}""", "falta o comando")]
    [InlineData("""{"name":"a","steps":[{"kind":"wait"}]}""", "maior que zero")]
    [InlineData("""{"name":"a","steps":[{"kind":"wait","seconds":2}]}""", "pelo menos um passo do tipo command")]
    [InlineData("""{"name":"a","steps":[{"kind":"wait_for","text":"x"},{"kind":"command","text":"ls"}]}""", "primeiro passo")]
    [InlineData("""{"name":"a","steps":[{"kind":"command","text":"ls"},{"kind":"wait","seconds":99999}]}""", "seconds deve ficar")]
    [InlineData("""{"name":"a","steps":[{"kind":"script","text":"ls"}]}""", "não existe")]
    [InlineData("""{"name":"a","steps":["ls"]}""", "objeto")]
    public void Build_RefusesWhatWouldNotRunAsRead(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Throws<MacroRequestException>(() => AssistantMacros.Build(document.RootElement, []));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Build_RefusesTooManySteps()
    {
        var steps = Enumerable.Range(0, AssistantMacros.MaxSteps + 1).Select(i => (object)new { kind = "command", text = "echo " + i }).ToArray();
        Assert.Throws<MacroRequestException>(() => AssistantMacros.Build(Json(new { name = "a", steps }), []));
    }

    [Fact]
    public void Build_SameNameNeedsReplaceId_AndReplaceKeepsTheId()
    {
        var existing = new List<CommandMacro> { new() { Id = "macro.abc", Name = "saúde do servidor", Steps = [MacroStep.Command("uptime")] } };
        var clash = Assert.Throws<MacroRequestException>(() => AssistantMacros.Build(Json(Health()), existing));
        Assert.Contains("macro.abc", clash.Message);

        var missing = Assert.Throws<MacroRequestException>(() => AssistantMacros.Build(Json(new { name = "x", replace_id = "macro.nada", steps = new[] { new { kind = "command", text = "ls" } } }), existing));
        Assert.Contains("list_macros", missing.Message);

        var replaced = AssistantMacros.Build(Json(new { name = "Saúde do servidor", replace_id = "macro.abc", steps = new[] { new { kind = "command", text = "free -h" } } }), existing);
        Assert.Equal("macro.abc", replaced.Id);
        Assert.Equal("free -h", replaced.Steps.Single().Text);
    }

    [Fact]
    public async Task Tools_SaveAndListGoThroughTheBook_AndSavingAsksInAskMode()
    {
        var book = new Book();
        var tools = new AssistantTools(new NoHost(), null, book);
        var call = new ToolCall("c1", AssistantTools.SaveMacro, Json(new
        {
            name = "Limpeza",
            steps = new object[] { new { kind = "command", text = "df -h /" }, new { kind = "command", text = "rm -rf /tmp/cache-app" } },
        }));

        var saved = await tools.ExecuteAsync(call, TestContext.Current.CancellationToken);
        Assert.False(saved.IsError, saved.Content);
        Assert.Contains("Macro criada", saved.Content);
        Assert.Contains("confirmação", saved.Content); // tem rm: avisa que o RootDock confirma ao executar
        Assert.Equal("Limpeza", book.Saved.Single().Name);

        var again = await tools.ExecuteAsync(call, TestContext.Current.CancellationToken);
        Assert.True(again.IsError);
        Assert.Contains("replace_id", again.Content);
        Assert.Single(book.Saved);

        var listed = await tools.ExecuteAsync(new ToolCall("c2", AssistantTools.ListMacros, Json(new { })), TestContext.Current.CancellationToken);
        Assert.Contains(book.Saved[0].Id, listed.Content);
        Assert.Contains("2. rm -rf /tmp/cache-app", listed.Content);

        var save = AssistantTools.Find(AssistantTools.SaveMacro)!;
        var list = AssistantTools.Find(AssistantTools.ListMacros)!;
        Assert.Equal(PermissionKind.Ask, AssistantPolicy.Evaluate(save, call.Input, AssistantMode.Ask).Kind);
        Assert.Equal(PermissionKind.Allow, AssistantPolicy.Evaluate(save, call.Input, AssistantMode.Auto).Kind);
        Assert.Equal(PermissionKind.Allow, AssistantPolicy.Evaluate(list, call.Input, AssistantMode.Ask).Kind);
    }

    [Fact]
    public async Task Tools_WithoutABook_ExplainInsteadOfFailing()
    {
        var result = await new AssistantTools(new NoHost()).ExecuteAsync(
            new ToolCall("c1", AssistantTools.ListMacros, Json(new { })), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("não estão disponíveis", result.Content);
    }

    [Fact]
    public void Preview_ShowsEveryStepAndFlagsRiskyOnes()
    {
        var preview = AssistantMacros.Preview(Json(new
        {
            description = "Reinicia o serviço.",
            steps = new object[] { new { kind = "command", text = "systemctl restart nginx" }, new { kind = "wait", seconds = 2 }, new { kind = "wait_for", text = "ok" } },
        }));
        Assert.StartsWith("Reinicia o serviço.", preview);
        Assert.Contains("1. ", preview);
        Assert.Contains("systemctl restart nginx", preview);
        Assert.Contains("2. [pausa de 2 s]", preview);
        Assert.Contains("3. [esperar aparecer \"ok\"]", preview);
    }

    private sealed class Book : IAssistantMacroBook
    {
        public List<CommandMacro> Saved { get; } = [];

        public IReadOnlyList<CommandMacro> List() => Saved.Select(m => m.Clone()).ToList();

        public void Save(CommandMacro macro)
        {
            Saved.RemoveAll(m => m.Id == macro.Id);
            Saved.Add(macro);
        }
    }

    private sealed class NoHost : IAssistantHost
    {
        public string Label => "teste";

        public string? TerminalDirectory => null;

        public Task<MultiExec.CommandResult> RunAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public Task<MultiExec.CommandResult> QueryAsync(string command, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public void Remember(string note)
        {
        }

        public string ReadTerminal(int lines) => "";

        public void TypeInTerminal(string text)
        {
        }

        public Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

        public Task WriteFileAsync(string path, byte[] content, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantFileEntry>> ListAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
