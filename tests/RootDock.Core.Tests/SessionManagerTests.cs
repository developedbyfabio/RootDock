using RootDock.App.Services;
using RootDock.Core.Sessions;

namespace RootDock.Core.Tests;

/// <summary>Exclusão e movimentação em lote (seleção múltipla da árvore) com Ctrl+Z.</summary>
public sealed class SessionManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rootdock-tests-" + Guid.NewGuid().ToString("N"));

    private SessionManager Create(params SessionEntry[] sessions)
    {
        Directory.CreateDirectory(_dir);
        var manager = new SessionManager(new SessionStore(Path.Combine(_dir, "sessions.json")), []);
        manager.Load();
        foreach (var session in sessions)
        {
            manager.AddOrUpdate(session);
        }

        return manager;
    }

    private static SessionEntry Session(string name, string folder = "") => new() { Name = name, Host = name, FolderPath = folder };

    [Fact]
    public void DeleteSessions_RemovesAll_AndUndoRestoresInOriginalOrder()
    {
        var a = Session("a");
        var b = Session("b");
        var c = Session("c");
        var d = Session("d");
        var manager = Create(a, b, c, d);

        Assert.Equal(2, manager.DeleteSessions([b.Id, d.Id]));
        Assert.Equal(["a", "c"], manager.Document.Sessions.Select(s => s.Name));

        Assert.NotNull(manager.Undo());
        Assert.Equal(["a", "b", "c", "d"], manager.Document.Sessions.Select(s => s.Name));
    }

    [Fact]
    public void Delete_Single_IsUndoable_AndPersisted()
    {
        var a = Session("a");
        var b = Session("b");
        var manager = Create(a, b);

        manager.Delete(a.Id);
        var reloaded = Create();
        Assert.Equal(["b"], reloaded.Document.Sessions.Select(s => s.Name));

        manager.Undo();
        Assert.Equal(["a", "b"], manager.Document.Sessions.Select(s => s.Name));
    }

    [Fact]
    public void MoveSessions_MovesAll_InOneUndoStep()
    {
        var a = Session("a");
        var b = Session("b", "Clientes");
        var c = Session("c");
        var manager = Create(a, b, c);
        manager.AddFolder("Produção");

        Assert.Equal(3, manager.MoveSessions([a.Id, b.Id, c.Id], "Produção"));
        Assert.All(manager.Document.Sessions, s => Assert.Equal("Produção", s.FolderPath));

        manager.Undo();
        Assert.Equal(["", "Clientes", ""], manager.Document.Sessions.Select(s => s.FolderPath));
        Assert.False(manager.CanUndo);
    }

    [Fact]
    public void MoveSessions_IgnoresSessionsAlreadyThere()
    {
        var a = Session("a", "X");
        var b = Session("b");
        var manager = Create(a, b);
        manager.AddFolder("X");

        Assert.Equal(1, manager.MoveSessions([a.Id, b.Id], "X"));
    }

    [Fact]
    public void BuildTree_ByCompany_CompanyThenBranch_WithoutCompanyLast()
    {
        var manager = Create(
            new SessionEntry { Name = "a", Company = "ACME", Branch = "SP" },
            new SessionEntry { Name = "b", Company = "acme" },
            new SessionEntry { Name = "c" },
            new SessionEntry { Name = "d", Company = "Beta", Branch = "RJ" });

        var roots = manager.BuildTree(null, (_, _) => { }, TreeGrouping.Company);

        Assert.Equal(["ACME", "Beta", "Sem empresa"], roots.Select(r => r.Name));
        Assert.All(roots.OfType<RootDock.App.ViewModels.FolderNode>(), f => Assert.True(f.IsVirtual));
        var acme = (RootDock.App.ViewModels.FolderNode)roots[0];
        Assert.Equal(2, acme.SessionCount);
        Assert.Equal("SP", acme.Children[0].Name);
        Assert.Equal("b", acme.Children[1].Name); // sem filial: depois das filiais
    }

    [Fact]
    public void BuildTree_ByEnvironment_ProductionFirst_SkipsEmpty()
    {
        var manager = Create(
            new SessionEntry { Name = "dev", Environment = SessionEnvironment.Development },
            new SessionEntry { Name = "prod", Environment = SessionEnvironment.Production });

        var roots = manager.BuildTree(null, (_, _) => { }, TreeGrouping.Environment);

        Assert.Equal(["Produção", "Desenvolvimento"], roots.Select(r => r.Name));
    }

    [Fact]
    public void SetCompany_TrimsAndClears()
    {
        var a = Session("a");
        var b = Session("b");
        var manager = Create(a, b);

        Assert.Equal(2, manager.SetCompany([a.Id, b.Id], "  ACME ", "SP"));
        Assert.All(manager.Document.Sessions, s => Assert.Equal(("ACME", "SP"), (s.Company, s.Branch)));

        manager.SetCompany([a.Id], " ", null);
        Assert.Null(manager.Document.Sessions[0].Company);
    }

    [Fact]
    public void Tags_SetInBulk_NoDuplicatesIgnoringCaseAndAccents()
    {
        var a = Session("a");
        var b = Session("b");
        a.Tags.Add("Produção");
        var manager = Create(a, b);

        Assert.Equal(1, manager.SetTag([a.Id, b.Id], "PRODUCAO", add: true)); // "a" já tinha
        Assert.Equal(["Produção"], manager.Document.Sessions[0].Tags);
        Assert.Equal(["PRODUCAO"], manager.Document.Sessions[1].Tags);
        Assert.Single(manager.AllTags());

        Assert.Equal(2, manager.SetTag([a.Id, b.Id], "produção", add: false));
        Assert.All(manager.Document.Sessions, s => Assert.Empty(s.Tags));
    }

    [Fact]
    public void Search_FindsByTag_AndHashSearchesOnlyTags()
    {
        var db = new SessionEntry { Name = "srv-01", Host = "10.0.0.1", Tags = ["BANCO"] };
        var web = new SessionEntry { Name = "banco-web", Host = "10.0.0.2" };
        var manager = Create(db, web);

        var all = manager.BuildTree("banco", (_, _) => { });
        Assert.Equal(2, all.OfType<RootDock.App.ViewModels.SessionNode>().Count());

        var onlyTag = manager.BuildTree("#banc", (_, _) => { });
        Assert.Equal("srv-01", Assert.Single(onlyTag.OfType<RootDock.App.ViewModels.SessionNode>()).Name);
    }

    [Fact]
    public void BuildTree_ByTags_SessionAppearsUnderEachTag_UntaggedLast()
    {
        var manager = Create(
            new SessionEntry { Name = "db", Tags = ["BANCO", "CRÍTICO"] },
            new SessionEntry { Name = "web", Tags = ["BANCO"] },
            new SessionEntry { Name = "tmp" });

        var roots = manager.BuildTree(null, (_, _) => { }, TreeGrouping.Tags);

        Assert.Equal(["BANCO", "CRÍTICO", "Sem tag"], roots.Select(r => r.Name));
        Assert.Equal(2, roots[0].Children.Count);
        Assert.Equal("db", Assert.Single(roots[1].Children).Name);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
