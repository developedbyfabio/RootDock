using RootDock.App.ViewModels;
using RootDock.Core.Import;
using RootDock.Core.Sessions;

namespace RootDock.App.Services;

/// <summary>Como a lista de sessões é agrupada.</summary>
public enum TreeGrouping
{
    /// <summary>Pastas criadas pelo usuário (arrastar e soltar organiza).</summary>
    Folders,

    /// <summary>Empresa › filial.</summary>
    Company,

    /// <summary>Produção, homologação, desenvolvimento.</summary>
    Environment,

    /// <summary>Uma seção por tag (a sessão aparece em cada tag que tiver).</summary>
    Tags,
}

/// <summary>Dono do documento de sessões: carrega, salva e monta a árvore exibida.</summary>
public sealed class SessionManager
{
    private readonly SessionStore _store;
    private readonly HashSet<string> _collapsed;

    public SessionManager(SessionStore store, IEnumerable<string> collapsedFolders)
    {
        _store = store;
        _collapsed = new HashSet<string>(collapsedFolders, StringComparer.OrdinalIgnoreCase);
    }

    public SessionDocument Document { get; private set; } = new();

    /// <summary>Arquivo de sessões inválido: nada é gravado até o usuário corrigir.</summary>
    public bool IsReadOnly { get; private set; }

    public IReadOnlyCollection<string> CollapsedFolders => _collapsed;

    public string FilePath => _store.FilePath;

    public void Load()
    {
        try
        {
            Document = _store.Load();
            IsReadOnly = false;
        }
        catch (SessionStoreCorruptedException)
        {
            Document = new SessionDocument();
            IsReadOnly = true;
            throw;
        }
    }

    public void Save()
    {
        if (!IsReadOnly)
        {
            _store.Save(Document);
        }
    }

    public bool Contains(Guid id) => Document.Sessions.Any(s => s.Id == id);

    public void AddOrUpdate(SessionEntry entry)
    {
        var index = Document.Sessions.FindIndex(s => s.Id == entry.Id);
        if (index >= 0)
        {
            Document.Sessions[index] = entry;
        }
        else
        {
            Document.Sessions.Add(entry);
        }

        Save();
    }

    public void Delete(Guid id) => DeleteSessions([id]);

    /// <summary>Exclui sessões guardando o estado para o Ctrl+Z. Retorna quantas saíram.</summary>
    public int DeleteSessions(IReadOnlyCollection<Guid> ids)
    {
        var snapshot = Snapshot();
        var removed = Document.Sessions
            .Select((session, index) => (Index: index, Entry: session))
            .Where(x => ids.Contains(x.Entry.Id))
            .ToList();
        if (removed.Count == 0)
        {
            return 0;
        }

        Document.Sessions.RemoveAll(s => ids.Contains(s.Id));
        Commit(snapshot, removed.Count == 1 ? $"excluir \"{removed[0].Entry.Name}\"" : $"excluir {removed.Count} sessões", removed);
        return removed.Count;
    }

    /// <summary>Move várias sessões para a mesma pasta (um único passo no Ctrl+Z).</summary>
    public int MoveSessions(IReadOnlyCollection<Guid> ids, string targetFolder)
    {
        var snapshot = Snapshot();
        var moved = ids.Count(id => SessionOrganizer.MoveSession(Document, id, targetFolder));
        if (moved == 0)
        {
            return 0;
        }

        ExpandPath(FolderPaths.Normalize(targetFolder));
        Commit(snapshot, moved == 1 ? "mover sessão" : $"mover {moved} sessões");
        return moved;
    }

    public void AddFolder(string path)
    {
        var normalized = FolderPaths.Normalize(path);
        if (normalized.Length > 0 && !Document.Folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            Document.Folders.Add(normalized);
            Save();
        }
    }

    /// <summary>Remove uma pasta somente se não houver sessões dentro dela (nem em subpastas).</summary>
    public bool TryDeleteEmptyFolder(string path)
    {
        var prefix = path + "/";
        var hasSessions = Document.Sessions.Any(s =>
            s.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            s.FolderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (hasSessions)
        {
            return false;
        }

        Document.Folders.RemoveAll(f =>
            f.Equals(path, StringComparison.OrdinalIgnoreCase) || f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        Save();
        return true;
    }

    /// <summary>Marca a conexão bem-sucedida. Sessões de conexão rápida (não salvas) são ignoradas.</summary>
    public void Touch(SessionEntry entry)
    {
        var saved = Document.Sessions.FirstOrDefault(s => s.Id == entry.Id);
        if (saved is null)
        {
            return;
        }

        saved.LastConnectedAt = DateTimeOffset.Now;
        saved.Username = entry.Username;
        Save();
    }

    // ------------------------------------------------------------------ reorganização (arrastar, mover, renomear)

    private const int MaxUndo = 30;
    private readonly Stack<UndoStep> _undo = new();

    private sealed record UndoStep(
        OrganizationSnapshot Organization, string[] Collapsed, string Description, List<(int Index, SessionEntry Entry)>? Removed);

    public bool CanUndo => _undo.Count > 0;

    public MoveCheck CheckMoveSession(Guid id, string targetFolder) =>
        SessionOrganizer.CheckMoveSession(Document, id, targetFolder);

    public MoveCheck CheckMoveFolder(string folder, string targetParent) =>
        SessionOrganizer.CheckMoveFolder(Document, folder, targetParent);

    public bool MoveSession(Guid id, string targetFolder)
    {
        var snapshot = Snapshot();
        if (!SessionOrganizer.MoveSession(Document, id, targetFolder))
        {
            return false;
        }

        // Mostra a sessão no novo lugar: abre a pasta de destino (e as acima dela).
        ExpandPath(FolderPaths.Normalize(targetFolder));
        Commit(snapshot, "mover sessão");
        return true;
    }

    public string? MoveFolder(string folder, string targetParent)
    {
        var snapshot = Snapshot();
        var moved = SessionOrganizer.MoveFolder(Document, folder, targetParent);
        if (moved is null)
        {
            return null;
        }

        RemapCollapsed(FolderPaths.Normalize(folder), moved);
        ExpandPath(FolderPaths.Normalize(targetParent));
        Commit(snapshot, "mover pasta");
        return moved;
    }

    public string? RenameFolder(string folder, string newName, out string? error)
    {
        var snapshot = Snapshot();
        var renamed = SessionOrganizer.RenameFolder(Document, folder, newName, out error);
        if (renamed is null || renamed == folder)
        {
            return renamed;
        }

        RemapCollapsed(FolderPaths.Normalize(folder), renamed);
        Commit(snapshot, "renomear pasta");
        return renamed;
    }

    /// <summary>Desfaz a última reorganização. Retorna a descrição do que foi desfeito.</summary>
    public string? Undo()
    {
        if (IsReadOnly || _undo.Count == 0)
        {
            return null;
        }

        var step = _undo.Pop();
        SessionOrganizer.Restore(Document, step.Organization);
        foreach (var (index, entry) in step.Removed ?? [])
        {
            if (Document.Sessions.All(s => s.Id != entry.Id))
            {
                Document.Sessions.Insert(Math.Min(index, Document.Sessions.Count), entry);
            }
        }

        _collapsed.Clear();
        _collapsed.UnionWith(step.Collapsed);
        Save();
        return step.Description;
    }

    private (OrganizationSnapshot, string[]) Snapshot() => (SessionOrganizer.Capture(Document), _collapsed.ToArray());

    private void Commit(
        (OrganizationSnapshot Organization, string[] Collapsed) before, string description, List<(int Index, SessionEntry Entry)>? removed = null)
    {
        _undo.Push(new UndoStep(before.Organization, before.Collapsed, description, removed));
        if (_undo.Count > MaxUndo)
        {
            // Mantém só os mais recentes.
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var item in keep)
            {
                _undo.Push(item);
            }
        }

        Save();
    }

    private void RemapCollapsed(string from, string to)
    {
        var remapped = _collapsed.Select(p => SessionOrganizer.Remap(p, from, to)).ToList();
        _collapsed.Clear();
        _collapsed.UnionWith(remapped);
    }

    private void ExpandPath(string folder)
    {
        for (var path = folder; path.Length > 0; path = FolderPaths.Parent(path))
        {
            _collapsed.Remove(path);
        }
    }

    public (int Added, ImportResult Result) ImportMobaXterm(string iniPath)
    {
        var result = MobaXtermImporter.ImportFile(iniPath);
        var added = SessionImportMerger.Merge(Document, result);
        if (added > 0)
        {
            Save();
        }

        return (added, result);
    }

    /// <summary>Importa os Hosts do ~/.ssh/config (ignora os que já existem).</summary>
    public (int Added, ImportResult Result) ImportSshConfig(string path)
    {
        var import = SshConfigImporter.Import(path);
        var result = new ImportResult(import.Sessions, [SshConfigImporter.Folder], import.Warnings);
        var added = SessionImportMerger.Merge(Document, result);

        // Bastion que já existia (não foi importado de novo): o ProxyJump aponta para a sessão existente.
        static bool Same(SessionEntry a, SessionEntry b) =>
            string.Equals(FolderPaths.Normalize(a.FolderPath), FolderPaths.Normalize(b.FolderPath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        foreach (var session in import.Sessions.Where(s => s.JumpSessionId is not null))
        {
            if (Document.Sessions.All(d => d.Id != session.JumpSessionId) &&
                import.Sessions.FirstOrDefault(b => b.Id == session.JumpSessionId) is { } bastion &&
                Document.Sessions.FirstOrDefault(d => Same(d, bastion)) is { } existing)
            {
                session.JumpSessionId = existing.Id;
            }
        }

        if (added > 0)
        {
            Save();
        }

        return (added, result);
    }

    public IReadOnlyList<TreeNode> BuildTree(string? filter, Action<FolderNode, bool> expandedChanged, TreeGrouping grouping = TreeGrouping.Folders)
    {
        if (grouping != TreeGrouping.Folders)
        {
            return BuildGroupedTree(filter, expandedChanged, grouping);
        }

        var text = filter?.Trim() ?? "";
        var filtering = text.Length > 0;
        var folders = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<TreeNode>();

        FolderNode GetFolder(string path)
        {
            if (folders.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var node = new FolderNode
            {
                Name = FolderPaths.LeafName(path),
                Path = path,
                IsExpanded = filtering || !_collapsed.Contains(path),
                ExpandedChanged = filtering ? null : expandedChanged,
            };
            folders[path] = node;
            var parent = FolderPaths.Parent(path);
            if (parent.Length == 0)
            {
                roots.Add(node);
            }
            else
            {
                GetFolder(parent).Children.Add(node);
            }

            return node;
        }

        if (!filtering)
        {
            foreach (var folder in Document.AllFolders())
            {
                GetFolder(folder);
            }
        }

        var sessions = Document.Sessions
            .Where(s => !filtering || Matches(s, text))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);

        var rootSessions = new List<TreeNode>();
        foreach (var session in sessions)
        {
            var node = new SessionNode { Name = session.Name, Session = session };
            if (session.FolderPath.Length == 0)
            {
                rootSessions.Add(node);
            }
            else
            {
                var folder = GetFolder(session.FolderPath);
                folder.Children.Add(node);
                for (var path = session.FolderPath; path.Length > 0; path = FolderPaths.Parent(path))
                {
                    folders[path].SessionCount++;
                }
            }
        }

        SortFolders(roots);
        if (roots.Count > 0 && rootSessions.Count > 0)
        {
            // Separa visualmente o que está dentro de pastas do que está solto na raiz.
            roots.Add(new SectionNode { Name = "SEM PASTA" });
        }

        return roots.Concat(rootSessions).ToList();
    }

    /// <summary>Árvore por empresa › filial ou por ambiente (grupos calculados, sem pastas).</summary>
    private IReadOnlyList<TreeNode> BuildGroupedTree(string? filter, Action<FolderNode, bool> expandedChanged, TreeGrouping grouping)
    {
        var text = filter?.Trim() ?? "";
        var filtering = text.Length > 0;
        var sessions = Document.Sessions
            .Where(s => !filtering || Matches(s, text))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FolderNode Group(string path, string name, int count) => new()
        {
            Name = name,
            Path = path,
            IsVirtual = true,
            SessionCount = count,
            IsExpanded = filtering || !_collapsed.Contains(path),
            ExpandedChanged = filtering ? null : expandedChanged,
        };

        static string Key(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        SessionNode Leaf(SessionEntry s) => new() { Name = s.Name, Session = s };

        var roots = new List<TreeNode>();
        if (grouping == TreeGrouping.Company)
        {
            var companies = sessions
                .GroupBy(s => Key(s.Company), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key.Length == 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var company in companies)
            {
                var companyPath = "§empresa/" + company.Key;
                var companyNode = Group(companyPath, company.Key.Length == 0 ? "Sem empresa" : company.Key, company.Count());
                var branches = company
                    .GroupBy(s => Key(s.Branch), StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key.Length == 0)
                    .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
                foreach (var branch in branches)
                {
                    if (branch.Key.Length == 0)
                    {
                        foreach (var session in branch)
                        {
                            companyNode.Children.Add(Leaf(session));
                        }

                        continue;
                    }

                    var branchNode = Group(companyPath + "/" + branch.Key, branch.Key, branch.Count());
                    foreach (var session in branch)
                    {
                        branchNode.Children.Add(Leaf(session));
                    }

                    companyNode.Children.Add(branchNode);
                }

                roots.Add(companyNode);
            }

            return roots;
        }

        if (grouping == TreeGrouping.Tags)
        {
            var tags = sessions.SelectMany(s => s.Tags)
                .GroupBy(SessionTags.Key)
                .Select(g => g.First())
                .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase);
            foreach (var tag in tags)
            {
                var tagged = sessions.Where(s => SessionTags.Has(s, tag)).ToList();
                var node = Group("§tag/" + SessionTags.Key(tag), tag, tagged.Count);
                foreach (var session in tagged)
                {
                    node.Children.Add(Leaf(session));
                }

                roots.Add(node);
            }

            var untagged = sessions.Where(s => s.Tags.Count == 0).ToList();
            if (untagged.Count > 0)
            {
                var node = Group("§tag/", "Sem tag", untagged.Count);
                foreach (var session in untagged)
                {
                    node.Children.Add(Leaf(session));
                }

                roots.Add(node);
            }

            return roots;
        }

        (SessionEnvironment Env, string Name)[] order =
        [
            (SessionEnvironment.Production, "Produção"),
            (SessionEnvironment.Staging, "Homologação"),
            (SessionEnvironment.Development, "Desenvolvimento"),
            (SessionEnvironment.None, "Ambiente não informado"),
        ];
        foreach (var (env, name) in order)
        {
            var group = sessions.Where(s => s.Environment == env).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var node = Group("§ambiente/" + env, name, group.Count);
            foreach (var session in group)
            {
                node.Children.Add(Leaf(session));
            }

            roots.Add(node);
        }

        return roots;
    }

    /// <summary>Todas as tags em uso (para sugerir no editor), sem repetir.</summary>
    public IReadOnlyList<string> AllTags() =>
        Document.Sessions.SelectMany(s => s.Tags)
            .GroupBy(SessionTags.Key)
            .Select(g => g.First())
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Põe (ou tira) uma tag de várias sessões. Retorna quantas mudaram.</summary>
    public int SetTag(IReadOnlyCollection<Guid> ids, string tag, bool add)
    {
        var changed = Document.Sessions.Where(s => ids.Contains(s.Id))
            .Count(s => add ? SessionTags.Add(s, tag) : SessionTags.Remove(s, tag));
        if (changed > 0)
        {
            Save();
        }

        return changed;
    }

    /// <summary>Empresa e filial de várias sessões de uma vez. Retorna quantas mudaram.</summary>
    public int SetCompany(IReadOnlyCollection<Guid> ids, string? company, string? branch)
    {
        var changed = 0;
        foreach (var session in Document.Sessions.Where(s => ids.Contains(s.Id)))
        {
            session.Company = string.IsNullOrWhiteSpace(company) ? null : company.Trim();
            session.Branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
            changed++;
        }

        if (changed > 0)
        {
            Save();
        }

        return changed;
    }

    public void SetCollapsed(string path, bool collapsed)
    {
        if (collapsed)
        {
            _collapsed.Add(path);
        }
        else
        {
            _collapsed.Remove(path);
        }
    }

    private static void SortFolders(List<TreeNode> nodes)
    {
        nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (var folder in nodes.OfType<FolderNode>())
        {
            var children = folder.Children.ToList();
            var subFolders = children.OfType<FolderNode>().Cast<TreeNode>().ToList();
            SortFolders(subFolders);
            folder.Children.Clear();
            foreach (var child in subFolders.Concat(children.OfType<SessionNode>()))
            {
                folder.Children.Add(child);
            }
        }
    }

    /// <summary>Busca: nome, servidor, usuário, pasta, empresa, filial, observações e tags. "#banco" busca só a tag.</summary>
    public static bool Matches(SessionEntry s, string text)
    {
        if (text.StartsWith('#') && text.Length > 1)
        {
            var key = SessionTags.Key(text[1..]);
            return s.Tags.Any(t => SessionTags.Key(t).StartsWith(key, StringComparison.Ordinal));
        }

        var plain = SessionTags.Key(text);
        return MatchesText(s, text) || s.Tags.Any(t => SessionTags.Key(t).Contains(plain, StringComparison.Ordinal));
    }

    private static bool MatchesText(SessionEntry s, string text) =>
        s.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        (s.Company?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (s.Branch?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        s.Host.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        (s.Username?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
        s.FolderPath.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        (s.Notes?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
