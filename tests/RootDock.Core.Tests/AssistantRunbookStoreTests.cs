using RootDock.Core.Assistant;
using Xunit;

namespace RootDock.Core.Tests;

public class AssistantRunbookStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rootdock-runbook-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static AssistantRunbookStore.Action Ran(string what, bool ok = true) => new(what, ok);

    [Fact]
    public void Append_WritesWhatWasAskedDoneAndHowItEnded()
    {
        var store = new AssistantRunbookStore(_folder);
        store.Append("srv:22", new DateTime(2026, 11, 26, 8, 40, 0), "Reinicie o\nnginx", [Ran("comando: systemctl restart nginx"), Ran("comando: curl -I localhost", false)], "O nginx voltou.\nTudo certo.", finished: true);
        var text = store.Tail("srv:22", 10_000);
        Assert.Equal(
            "## 2026-11-26 08:40 · Reinicie o nginx\n- comando: systemctl restart nginx\n- comando: curl -I localhost (falhou)\nResultado: O nginx voltou. Tudo certo.",
            text);
        Assert.Equal("", store.Tail("outro:22", 10_000));
    }

    [Fact]
    public void Append_WithoutActions_WritesNothing()
    {
        var store = new AssistantRunbookStore(_folder);
        store.Append("srv:22", DateTime.Now, "qual a versão do php?", [], "8.3", finished: true);
        Assert.Equal("", store.Tail("srv:22", 10_000));
        Assert.False(File.Exists(store.PathOf("srv:22")));
    }

    [Fact]
    public void Append_Interrupted_SaysSo_AndLongListsAreCut()
    {
        var store = new AssistantRunbookStore(_folder);
        var actions = Enumerable.Range(1, AssistantRunbookStore.MaxActionsPerEntry + 5).Select(i => Ran("comando: echo " + i)).ToList();
        store.Append("srv:22", DateTime.Now, "muita coisa", actions, "texto parcial", finished: false);
        var text = store.Tail("srv:22", 100_000);
        Assert.Contains("- ... e mais 5 ações", text);
        Assert.DoesNotContain("echo " + (AssistantRunbookStore.MaxActionsPerEntry + 1), text);
        Assert.EndsWith("Situação: interrompido antes de terminar.", text);
        Assert.DoesNotContain("Resultado:", text);
    }

    [Fact]
    public void Tail_ReturnsOnlyWholeRecentEntries()
    {
        var store = new AssistantRunbookStore(_folder);
        for (var i = 1; i <= 30; i++)
        {
            store.Append("srv:22", new DateTime(2026, 11, i, 9, 0, 0), "pedido " + i, [Ran("comando: passo " + i)], "feito " + i, finished: true);
        }

        var tail = store.Tail("srv:22", 400);
        Assert.StartsWith("## 2026-11-", tail);
        Assert.EndsWith("Resultado: feito 30", tail);
        Assert.DoesNotContain("pedido 1\n", tail);
        Assert.True(tail.Length <= 400);
    }

    [Fact]
    public void Append_KeepsTheFileBounded()
    {
        var store = new AssistantRunbookStore(_folder);
        var big = new string('x', 280);
        for (var i = 0; i < 2600; i++)
        {
            store.Append("srv:22", DateTime.Now, "pedido " + i, [Ran("comando: " + big)], null, finished: true);
        }

        var text = File.ReadAllText(store.PathOf("srv:22"));
        Assert.True(text.Length <= AssistantRunbookStore.MaxFileChars);
        Assert.StartsWith("## ", text);
        Assert.Contains("pedido 2599", text);
        Assert.DoesNotContain("pedido 0\n", text);
    }
}
