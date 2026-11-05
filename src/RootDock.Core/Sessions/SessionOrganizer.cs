namespace RootDock.Core.Sessions;

public enum MoveCheck
{
    /// <summary>Pode mover.</summary>
    Allowed,

    /// <summary>Já está nesse lugar: nada a fazer.</summary>
    NoChange,

    /// <summary>Pasta para dentro dela mesma ou de uma subpasta.</summary>
    IntoItself,

    /// <summary>O destino já tem uma pasta com esse nome: o conteúdo será mesclado.</summary>
    WouldMerge,

    /// <summary>Origem inexistente (sessão ou pasta removida).</summary>
    NotFound,
}

/// <summary>Estado de pastas e posições das sessões, para desfazer uma reorganização.</summary>
public sealed class OrganizationSnapshot
{
    internal OrganizationSnapshot(List<string> folders, Dictionary<Guid, string> sessionFolders)
    {
        Folders = folders;
        SessionFolders = sessionFolders;
    }

    internal List<string> Folders { get; }

    internal Dictionary<Guid, string> SessionFolders { get; }
}

/// <summary>
/// Reorganização da árvore de sessões (mover sessões e pastas, renomear pastas).
/// Pastas são caminhos ("A/B"); mover uma pasta reescreve o prefixo dela e de tudo abaixo.
/// </summary>
public static class SessionOrganizer
{
    public static OrganizationSnapshot Capture(SessionDocument document) =>
        new([.. document.Folders], document.Sessions.ToDictionary(s => s.Id, s => s.FolderPath));

    /// <summary>Volta pastas e posições ao estado capturado. Sessões criadas depois ficam onde estão.</summary>
    public static void Restore(SessionDocument document, OrganizationSnapshot snapshot)
    {
        document.Folders = [.. snapshot.Folders];
        foreach (var session in document.Sessions)
        {
            if (snapshot.SessionFolders.TryGetValue(session.Id, out var folder))
            {
                session.FolderPath = folder;
            }
        }
    }

    public static MoveCheck CheckMoveSession(SessionDocument document, Guid sessionId, string targetFolder)
    {
        var session = document.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null)
        {
            return MoveCheck.NotFound;
        }

        return Same(session.FolderPath, FolderPaths.Normalize(targetFolder)) ? MoveCheck.NoChange : MoveCheck.Allowed;
    }

    public static bool MoveSession(SessionDocument document, Guid sessionId, string targetFolder)
    {
        if (CheckMoveSession(document, sessionId, targetFolder) != MoveCheck.Allowed)
        {
            return false;
        }

        var target = FolderPaths.Normalize(targetFolder);
        document.Sessions.First(s => s.Id == sessionId).FolderPath = target;
        EnsureFolder(document, target);
        return true;
    }

    public static MoveCheck CheckMoveFolder(SessionDocument document, string folder, string targetParent)
    {
        var source = FolderPaths.Normalize(folder);
        var parent = FolderPaths.Normalize(targetParent);
        if (source.Length == 0 || !Exists(document, source))
        {
            return MoveCheck.NotFound;
        }

        if (Same(source, parent) || IsInside(parent, source))
        {
            return MoveCheck.IntoItself;
        }

        if (Same(FolderPaths.Parent(source), parent))
        {
            return MoveCheck.NoChange;
        }

        var destination = Combine(parent, FolderPaths.LeafName(source));
        return Exists(document, destination) ? MoveCheck.WouldMerge : MoveCheck.Allowed;
    }

    /// <summary>Move a pasta (com subpastas e sessões) para dentro de <paramref name="targetParent"/>. Retorna o novo caminho.</summary>
    public static string? MoveFolder(SessionDocument document, string folder, string targetParent)
    {
        var check = CheckMoveFolder(document, folder, targetParent);
        if (check is not (MoveCheck.Allowed or MoveCheck.WouldMerge))
        {
            return null;
        }

        var source = FolderPaths.Normalize(folder);
        var destination = Combine(FolderPaths.Normalize(targetParent), FolderPaths.LeafName(source));
        Rewrite(document, source, destination);
        return destination;
    }

    /// <summary>Renomeia a pasta. Falha (retorna motivo) se o nome for inválido ou já existir no mesmo nível.</summary>
    public static string? RenameFolder(SessionDocument document, string folder, string newName, out string? error)
    {
        error = null;
        var source = FolderPaths.Normalize(folder);
        var name = newName.Trim();
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\'))
        {
            error = "Use um nome sem \"/\" nem \"\\\".";
            return null;
        }

        if (!Exists(document, source))
        {
            error = "A pasta não existe mais.";
            return null;
        }

        var destination = Combine(FolderPaths.Parent(source), name);
        if (Same(destination, source) && destination == source)
        {
            return source;
        }

        // Só muda maiúsculas/minúsculas: permitido. Outro nome já existente: recusado.
        if (!Same(destination, source) && Exists(document, destination))
        {
            error = $"Já existe uma pasta \"{name}\" neste nível.";
            return null;
        }

        Rewrite(document, source, destination);
        return destination;
    }

    /// <summary>Caminho equivalente depois de mover/renomear <paramref name="from"/> para <paramref name="to"/>.</summary>
    public static string Remap(string path, string from, string to)
    {
        if (Same(path, from))
        {
            return to;
        }

        return IsInside(path, from) ? to + path[from.Length..] : path;
    }

    /// <summary>Verdadeiro se <paramref name="path"/> está dentro de <paramref name="folder"/> (não igual).</summary>
    public static bool IsInside(string path, string folder) =>
        folder.Length > 0 && path.Length > folder.Length &&
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static void Rewrite(SessionDocument document, string from, string to)
    {
        document.Folders = document.Folders
            .Select(f => Remap(FolderPaths.Normalize(f), from, to))
            .Append(to)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var session in document.Sessions)
        {
            session.FolderPath = Remap(FolderPaths.Normalize(session.FolderPath), from, to);
        }
    }

    private static void EnsureFolder(SessionDocument document, string folder)
    {
        if (folder.Length > 0 && !document.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            document.Folders.Add(folder);
        }
    }

    private static bool Exists(SessionDocument document, string folder) =>
        document.AllFolders().Contains(folder, StringComparer.OrdinalIgnoreCase);

    private static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
