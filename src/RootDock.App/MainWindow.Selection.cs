using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RootDock.App.Services;
using RootDock.App.ViewModels;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RootDock.App;

/// <summary>
/// Seleção de várias sessões na árvore: Ctrl+clique alterna, Shift+clique pega o intervalo,
/// Ctrl+A pega todas as visíveis. Com várias marcadas: Del exclui, Enter abre, arrastar move,
/// botão direito oferece abrir em grade, mover e excluir. Esc limpa.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<Guid> _picked = new();
    private Guid? _pickAnchor;
    private SessionNode? _pendingSingle;
    private bool _menuForPicked;

    private bool HasMultiSelection => _picked.Count > 1;

    /// <summary>Trata Ctrl/Shift+clique. Retorna true quando o clique já foi resolvido aqui.</summary>
    private bool HandleSelectionClick(MouseButtonEventArgs e, TreeNode? node)
    {
        _pendingSingle = null;
        var mods = Keyboard.Modifiers;
        if (node is SessionNode session && mods.HasFlag(ModifierKeys.Control))
        {
            if (_picked.Count == 0 && SessionTree.SelectedItem is SessionNode current && current != session)
            {
                _picked.Add(current.Session.Id);
            }

            if (_picked.Remove(session.Session.Id))
            {
                session.IsSelected = false;
            }
            else
            {
                _picked.Add(session.Session.Id);
            }

            _pickAnchor = session.Session.Id;
            ApplyPicked();
            FocusContainer(session);
            e.Handled = true;
            return true;
        }

        if (node is SessionNode target && mods.HasFlag(ModifierKeys.Shift))
        {
            var visible = VisibleSessions();
            var anchorId = _pickAnchor ?? (SessionTree.SelectedItem as SessionNode)?.Session.Id;
            var to = visible.IndexOf(target);
            var from = visible.FindIndex(s => s.Session.Id == anchorId);
            if (from < 0)
            {
                from = to;
            }

            _picked.Clear();
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                _picked.Add(visible[i].Session.Id);
            }

            _pickAnchor ??= target.Session.Id;
            ApplyPicked();
            FocusContainer(target);
            e.Handled = true;
            return true;
        }

        if (node is SessionNode plain && HasMultiSelection && _picked.Contains(plain.Session.Id))
        {
            // Pode ser o começo de um arraste do grupo: decide ao soltar o botão.
            _pendingSingle = plain;
            return false;
        }

        _pickAnchor = node is SessionNode anchor ? anchor.Session.Id : null;
        ClearPicked();
        return false;
    }

    private void OnTreeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingle is { } node && !_dragging)
        {
            ClearPicked();
            node.IsSelected = true;
            _pickAnchor = node.Session.Id;
        }

        _pendingSingle = null;
    }

    /// <summary>Teclas da seleção múltipla. Retorna true quando tratou a tecla.</summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _picked.Clear();
            foreach (var node in VisibleSessions())
            {
                _picked.Add(node.Session.Id);
            }

            ApplyPicked();
            return true;
        }

        if (!HasMultiSelection)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Delete:
                _ = DeleteSessionsAsync(PickedEntries());
                return true;
            case Key.Enter:
                ConnectMany(PickedEntries(), grid: false);
                return true;
            case Key.Escape:
                ClearPicked();
                return true;
            case Key.Up or Key.Down or Key.Home or Key.End when Keyboard.Modifiers == ModifierKeys.None:
                ClearPicked();
                return false;
            default:
                return false;
        }
    }

    private void ClearPicked()
    {
        if (_picked.Count == 0)
        {
            return;
        }

        _picked.Clear();
        ApplyPicked();
    }

    /// <summary>Reflete a seleção nos itens da árvore (também depois de reconstruí-la).</summary>
    private void ApplyPicked()
    {
        var existing = _sessions.Document.Sessions.Select(s => s.Id).ToHashSet();
        _picked.IntersectWith(existing);
        if (_picked.Count == 1 && FindNode(TreeNodes(), n => n is SessionNode s && _picked.Contains(s.Session.Id)) is SessionNode only)
        {
            // Sobrou uma: vira a seleção normal.
            _picked.Clear();
            only.IsSelected = true;
        }

        void Walk(IEnumerable<TreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsInSelection = node is SessionNode s && _picked.Contains(s.Session.Id);
                Walk(node.Children);
            }
        }

        Walk(TreeNodes());
        if (HasMultiSelection)
        {
            SummaryText.Text = $"{_picked.Count} sessões selecionadas  ·  Del exclui  ·  arraste para mover  ·  botão direito: mais opções  ·  Esc limpa";
        }
        else
        {
            UpdateSummary();
        }
    }

    private IEnumerable<TreeNode> TreeNodes() => SessionTree.ItemsSource as IEnumerable<TreeNode> ?? [];

    /// <summary>Sessões na ordem em que aparecem (dentro de pastas abertas).</summary>
    private List<SessionNode> VisibleSessions()
    {
        var list = new List<SessionNode>();
        void Walk(IEnumerable<TreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is SessionNode session)
                {
                    list.Add(session);
                }
                else if (node is FolderNode { IsExpanded: true } folder)
                {
                    Walk(folder.Children);
                }
            }
        }

        Walk(TreeNodes());
        return list;
    }

    /// <summary>Sessões marcadas, na ordem da árvore.</summary>
    private List<SessionEntry> PickedEntries()
    {
        var order = new List<SessionEntry>();
        void Walk(IEnumerable<TreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is SessionNode s && _picked.Contains(s.Session.Id))
                {
                    order.Add(s.Session);
                }

                Walk(node.Children);
            }
        }

        Walk(TreeNodes());
        return order;
    }

    private void FocusContainer(TreeNode node)
    {
        if (FindContainer(node) is { } container)
        {
            container.Focus();
        }
    }

    // ------------------------------------------------------------------ ações em lote

    /// <summary>Abre várias sessões: cada uma na sua aba, ou todas juntas numa aba em grade.</summary>
    private void ConnectMany(IReadOnlyList<SessionEntry> entries, bool grid)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (!grid)
        {
            foreach (var entry in entries)
            {
                Connect(entry);
            }

            return;
        }

        // Área de Trabalho Remota abre na própria aba; os terminais vão juntos para a grade.
        foreach (var graphical in entries.Where(e => e.Kind is SessionKind.Rdp or SessionKind.Vnc or SessionKind.Ftp))
        {
            Connect(graphical);
        }

        var terminals = entries.Where(e => e.Kind is not (SessionKind.Rdp or SessionKind.Vnc or SessionKind.Ftp)).ToList();
        if (terminals.Count == 0)
        {
            return;
        }

        var first = _workspace.Open(terminals[0]);
        var previous = first;
        foreach (var entry in terminals.Skip(1))
        {
            previous = _workspace.Open(entry, previous.Id, "v");
        }

        _workspace.ArrangeGrid(first);
    }

    /// <summary>Exclui uma ou várias sessões (Ctrl+Z traz de volta, sem as senhas salvas).</summary>
    private async Task DeleteSessionsAsync(IReadOnlyList<SessionEntry> entries)
    {
        if (entries.Count == 0 || !EnsureWritable())
        {
            return;
        }

        string text;
        if (entries.Count == 1)
        {
            text = $"Excluir a sessão \"{entries[0].Name}\" ({entries[0].Endpoint})?";
        }
        else
        {
            var names = entries.Take(8).Select(e => $"•  {e.Name}" + (e.Name == e.Endpoint ? "" : $"  ({e.Endpoint})"));
            var more = entries.Count > 8 ? $"\n•  e mais {entries.Count - 8}" : "";
            text = $"Excluir {entries.Count} sessões?\n\n{string.Join("\n", names)}{more}";
        }

        var box = new MessageBox
        {
            Title = entries.Count == 1 ? "Excluir sessão" : $"Excluir {entries.Count} sessões",
            Content = text + "\n\nAs senhas salvas no Cofre do Windows também serão removidas. Terminais abertos continuam abertos. " +
                      "Ctrl+Z na lista traz as sessões de volta (sem as senhas).",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar",
        };
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary)
        {
            return;
        }

        var removed = _sessions.DeleteSessions(entries.Select(e => e.Id).ToList());
        foreach (var entry in entries)
        {
            try
            {
                _logins.DeleteAll(entry);
                _vault.Delete(CredentialVault.KeyPassphraseKey(entry.Id));
            }
            catch (Win32Exception ex)
            {
                Log.Error("Remover credenciais da sessão excluída", ex);
            }
        }

        _picked.Clear();
        AfterReorganize((removed == 1 ? "Sessão excluída." : $"{removed} sessões excluídas.") + "  Ctrl+Z desfaz.", null);
    }

    /// <summary>Outras sessões com exatamente a mesma conexão (mesmo servidor, usuário e configuração).</summary>
    private List<SessionEntry> CopiesOf(SessionEntry entry) =>
        _sessions.Document.Sessions.Where(s => s.Id != entry.Id && SameConnection(s, entry)).ToList();

    /// <summary>
    /// Duas sessões são "a mesma" quando conectam do mesmo jeito: tipo, servidor, porta, usuário,
    /// chave, ambiente e comando inicial. Nome e pasta são só organização.
    /// </summary>
    private static bool SameConnection(SessionEntry a, SessionEntry b) =>
        a.Kind == b.Kind &&
        a.LocalShell == b.LocalShell &&
        string.Equals(a.Host?.Trim(), b.Host?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port &&
        string.Equals(Blank(a.Username), Blank(b.Username), StringComparison.Ordinal) &&
        string.Equals(Blank(a.PrivateKeyPath), Blank(b.PrivateKeyPath), StringComparison.OrdinalIgnoreCase) &&
        a.Environment == b.Environment &&
        a.JumpSessionId == b.JumpSessionId &&
        a.UseAgent == b.UseAgent &&
        string.Equals(Blank(a.StartupCommand), Blank(b.StartupCommand), StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Submenu "Tags": marcar/desmarcar tags existentes e criar uma nova (vale para uma ou várias sessões).</summary>
    private MenuItem TagMenu(IReadOnlyList<SessionEntry> entries)
    {
        var menu = new MenuItem { Header = "Tags" };
        var ids = entries.Select(e => e.Id).ToList();
        foreach (var tag in _sessions.AllTags())
        {
            var all = entries.All(e => SessionTags.Has(e, tag));
            var some = !all && entries.Any(e => SessionTags.Has(e, tag));
            var chip = TagColors.Chip(tag);
            var item = new MenuItem
            {
                Header = some ? $"{tag}  (em parte)" : tag,
                IsCheckable = true,
                IsChecked = all,
                Foreground = chip.Foreground,
            };
            item.Click += (_, _) =>
            {
                _sessions.SetTag(ids, tag, add: !all);
                RebuildTree();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MenuItem("Nova tag...", () => _ = NewTagAsync(entries)));
        return menu;
    }

    private async Task NewTagAsync(IReadOnlyList<SessionEntry> entries)
    {
        if (!EnsureWritable() ||
            await Prompts.AskTextAsync(entries.Count == 1 ? $"Nova tag em \"{entries[0].Name}\"" : $"Nova tag nas {entries.Count} sessões",
                "ex.: BANCO, CRÍTICO, BACKUP", "", "Adicionar") is not { } text ||
            SessionTags.Clean(text) is not { } tag)
        {
            return;
        }

        var changed = _sessions.SetTag(entries.Select(e => e.Id).ToList(), tag, add: true);
        RebuildTree();
        Notify(changed <= 1 ? $"Tag \"{tag}\" adicionada." : $"Tag \"{tag}\" adicionada em {changed} sessões.", false);
    }

    /// <summary>Menu de um grupo calculado (empresa, filial, ambiente).</summary>
    private void AddGroupMenu(ContextMenu menu, FolderNode group)
    {
        var entries = new List<SessionEntry>();
        void Walk(TreeNode node)
        {
            if (node is SessionNode s)
            {
                entries.Add(s.Session);
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(group);
        menu.Items.Add(MenuItem($"Abrir as {entries.Count} juntas na mesma tela", () => ConnectMany(entries, grid: true)));
        menu.Items.Add(MenuItem($"Conectar as {entries.Count} (uma aba cada)", () => ConnectMany(entries, grid: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"Selecionar as {entries.Count}", () =>
        {
            _picked.Clear();
            _picked.UnionWith(entries.Select(e => e.Id));
            group.IsExpanded = true;
            ApplyPicked();
        }));
        menu.Items.Add(MenuItem($"Definir empresa e filial das {entries.Count}...", () => _ = SetCompanyAsync(entries)));
        if (entries.Any(e => e.Kind == SessionKind.Ssh))
        {
            menu.Items.Add(MenuItem("Executar um comando nos servidores desta pasta...", () => OpenMultiExec(entries)));
        }
    }

    /// <summary>Empresa e filial de várias sessões de uma vez.</summary>
    private async Task SetCompanyAsync(IReadOnlyList<SessionEntry> entries)
    {
        if (entries.Count == 0 || !EnsureWritable())
        {
            return;
        }

        static string? Common(IEnumerable<string?> values)
        {
            var distinct = values.Select(v => v ?? "").Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        var companies = _sessions.Document.Sessions.Select(s => s.Company).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).ToList();
        var branches = _sessions.Document.Sessions.Select(s => s.Branch).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).ToList();
        var company = new ComboBox { IsEditable = true, ItemsSource = companies, Text = Common(entries.Select(e => e.Company)) ?? "", MinWidth = 320 };
        var branch = new ComboBox { IsEditable = true, ItemsSource = branches, Text = Common(entries.Select(e => e.Branch)) ?? "", MinWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Empresa (vazio = sem empresa)" });
        panel.Children.Add(company);
        panel.Children.Add(new TextBlock { Text = "Filial", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(branch);
        var box = new MessageBox
        {
            Title = entries.Count == 1 ? $"Empresa e filial de \"{entries[0].Name}\"" : $"Empresa e filial das {entries.Count} sessões",
            Content = panel,
            PrimaryButtonText = "Aplicar",
            CloseButtonText = "Cancelar",
        };
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary)
        {
            return;
        }

        var changed = _sessions.SetCompany(entries.Select(e => e.Id).ToList(), company.Text, branch.Text);
        RebuildTree();
        Notify(changed == 1 ? "Empresa e filial atualizadas." : $"Empresa e filial atualizadas em {changed} sessões.", false);
    }

    private void AddSelectionMenu(ContextMenu menu)
    {
        var entries = PickedEntries();
        var ids = entries.Select(e => e.Id).ToList();
        menu.Items.Add(MenuItem($"Conectar as {entries.Count} (uma aba cada)", () => ConnectMany(entries, grid: false), "Enter"));
        menu.Items.Add(MenuItem($"Abrir as {entries.Count} juntas na mesma tela", () => ConnectMany(entries, grid: true)));
        menu.Items.Add(MoveToMenu(
            target => ids.Any(id => _sessions.CheckMoveSession(id, target) == MoveCheck.Allowed),
            target => _ = ApplyMoveAsync(new TreeDragData(null, null, $"{ids.Count} sessões", ids), target)));
        menu.Items.Add(MenuItem($"Definir empresa e filial das {entries.Count}...", () => _ = SetCompanyAsync(entries)));
        menu.Items.Add(TagMenu(entries));
        if (entries.Any(e => e.Kind == SessionKind.Ssh))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"Executar um comando nas {entries.Count}...", () => OpenMultiExec(entries)));
            menu.Items.Add(MenuItem($"Instalar minha chave SSH nas {entries.Count}...", () => _ = InstallKeyAsync(entries)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"Excluir {entries.Count} sessões...", () => _ = DeleteSessionsAsync(entries), "Del"));
    }
}
