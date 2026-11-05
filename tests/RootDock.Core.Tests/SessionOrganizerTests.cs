using RootDock.Core.Sessions;

namespace RootDock.Core.Tests;

public class SessionOrganizerTests
{
    private static SessionDocument Sample(out SessionEntry web, out SessionEntry db, out SessionEntry root)
    {
        web = new SessionEntry { Name = "web", Host = "10.0.0.1", FolderPath = "Empresa/Producao" };
        db = new SessionEntry { Name = "db", Host = "10.0.0.2", FolderPath = "Empresa/Producao/Banco" };
        root = new SessionEntry { Name = "solta", Host = "10.0.0.3" };
        return new SessionDocument { Folders = ["Empresa", "FH", "Vazia"], Sessions = [web, db, root] };
    }

    [Fact]
    public void Moves_a_session_into_a_folder_and_back_to_root()
    {
        var doc = Sample(out _, out _, out var root);

        Assert.True(SessionOrganizer.MoveSession(doc, root.Id, "FH"));
        Assert.Equal("FH", root.FolderPath);

        Assert.Equal(MoveCheck.NoChange, SessionOrganizer.CheckMoveSession(doc, root.Id, "fh"));
        Assert.True(SessionOrganizer.MoveSession(doc, root.Id, ""));
        Assert.Equal("", root.FolderPath);
    }

    [Fact]
    public void Moving_a_folder_carries_subfolders_and_sessions()
    {
        var doc = Sample(out var web, out var db, out _);

        var moved = SessionOrganizer.MoveFolder(doc, "Empresa/Producao", "FH");

        Assert.Equal("FH/Producao", moved);
        Assert.Equal("FH/Producao", web.FolderPath);
        Assert.Equal("FH/Producao/Banco", db.FolderPath);
        Assert.DoesNotContain("Empresa/Producao", doc.AllFolders());
        Assert.Contains("Empresa", doc.AllFolders()); // a pasta de origem continua existindo
    }

    [Fact]
    public void Moving_a_folder_to_root_and_detecting_no_change()
    {
        var doc = Sample(out var web, out _, out _);

        Assert.Equal(MoveCheck.NoChange, SessionOrganizer.CheckMoveFolder(doc, "Empresa/Producao", "Empresa"));
        Assert.Equal("Producao", SessionOrganizer.MoveFolder(doc, "Empresa/Producao", ""));
        Assert.Equal("Producao", web.FolderPath);
    }

    [Theory]
    [InlineData("Empresa", "Empresa")]
    [InlineData("Empresa", "Empresa/Producao")]
    [InlineData("Empresa", "Empresa/Producao/Banco")]
    public void Refuses_to_move_a_folder_into_itself(string folder, string target)
    {
        var doc = Sample(out _, out _, out _);

        Assert.Equal(MoveCheck.IntoItself, SessionOrganizer.CheckMoveFolder(doc, folder, target));
        Assert.Null(SessionOrganizer.MoveFolder(doc, folder, target));
    }

    [Fact]
    public void Similar_prefix_is_not_treated_as_inside()
    {
        Assert.False(SessionOrganizer.IsInside("EmpresaX/A", "Empresa"));
        Assert.True(SessionOrganizer.IsInside("Empresa/A", "Empresa"));
        Assert.Equal("Novo/A", SessionOrganizer.Remap("Empresa/A", "Empresa", "Novo"));
        Assert.Equal("EmpresaX/A", SessionOrganizer.Remap("EmpresaX/A", "Empresa", "Novo"));
    }

    [Fact]
    public void Detects_merge_when_target_has_folder_with_same_name()
    {
        var doc = Sample(out var web, out _, out _);
        doc.Folders.Add("FH/Producao");

        Assert.Equal(MoveCheck.WouldMerge, SessionOrganizer.CheckMoveFolder(doc, "Empresa/Producao", "FH"));
        Assert.Equal("FH/Producao", SessionOrganizer.MoveFolder(doc, "Empresa/Producao", "FH"));
        Assert.Equal("FH/Producao", web.FolderPath);
        Assert.Single(doc.Folders, f => f == "FH/Producao");
    }

    [Fact]
    public void Rename_folder_rewrites_descendants_and_rejects_conflicts()
    {
        var doc = Sample(out var web, out var db, out _);

        Assert.Equal("Empresa/Prod", SessionOrganizer.RenameFolder(doc, "Empresa/Producao", "Prod", out var error));
        Assert.Null(error);
        Assert.Equal("Empresa/Prod", web.FolderPath);
        Assert.Equal("Empresa/Prod/Banco", db.FolderPath);

        Assert.Null(SessionOrganizer.RenameFolder(doc, "FH", "Empresa", out error));
        Assert.NotNull(error);
        Assert.Null(SessionOrganizer.RenameFolder(doc, "FH", "a/b", out error));
        Assert.Equal("fh", SessionOrganizer.RenameFolder(doc, "FH", "fh", out _)); // só caixa: permitido
    }

    [Fact]
    public void Snapshot_restores_previous_organization()
    {
        var doc = Sample(out var web, out var db, out var root);
        var before = SessionOrganizer.Capture(doc);

        SessionOrganizer.MoveFolder(doc, "Empresa/Producao", "FH");
        SessionOrganizer.MoveSession(doc, root.Id, "Vazia");
        SessionOrganizer.Restore(doc, before);

        Assert.Equal("Empresa/Producao", web.FolderPath);
        Assert.Equal("Empresa/Producao/Banco", db.FolderPath);
        Assert.Equal("", root.FolderPath);
        Assert.Equal(["Empresa", "FH", "Vazia"], doc.Folders);
    }
}
