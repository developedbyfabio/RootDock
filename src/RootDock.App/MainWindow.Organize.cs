using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RootDock.App.Services;
using RootDock.App.ViewModels;
using RootDock.Core.Sessions;
using MenuItem = System.Windows.Controls.MenuItem;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace RootDock.App;

/// <summary>Item arrastado na árvore de sessões (uma sessão, uma pasta ou várias sessões marcadas).</summary>
internal sealed record TreeDragData(Guid? SessionId, string? FolderPath, string Name, IReadOnlyList<Guid>? SessionIds = null);

/// <summary>
/// Organização da árvore de sessões: arrastar e soltar (sessões e pastas), mover pelo menu,
/// renomear pastas e desfazer (Ctrl+Z).
/// </summary>
public partial class MainWindow
{
    private const string DragFormat = "RootDock.TreeItem";
    private const double AutoScrollMargin = 28;

    private readonly Dictionary<string, FolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _expandTimer;
    private FolderNode? _expandCandidate;
    private TreeNode? _dragCandidate;
    private TreeNode? _dropHighlight;
    private Point _dragStart;
    private bool _dragging;
    private ScrollViewer? _treeScroll;

    private void InitTreeOrganization()
    {
        // Parar sobre uma pasta fechada durante o arraste abre a pasta.
        _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (_dragging && _expandCandidate is { IsExpanded: false } folder)
            {
                folder.IsExpanded = true;
            }
        };
    }

    /// <summary>Recria o mapa caminho → nó de pasta (para destacar o destino de uma sessão).</summary>
    private void IndexFolderNodes(IEnumerable<TreeNode> nodes)
    {
        _folderNodes.Clear();
        void Walk(IEnumerable<TreeNode> list)
        {
            foreach (var node in list)
            {
                if (node is FolderNode folder)
                {
                    _folderNodes[folder.Path] = folder;
                    Walk(folder.Children);
                }
            }
        }

        Walk(nodes);
    }

    // ------------------------------------------------------------------ início do arraste

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        // A setinha de abrir/fechar pasta não inicia arraste.
        if (FindAncestor<ToggleButton>(e.OriginalSource) is not null)
        {
            return;
        }

        var node = FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext as TreeNode;
        if (HandleSelectionClick(e, node))
        {
            return;
        }

        if (node is not null)
        {
            _dragStart = e.GetPosition(SessionTree);
            _dragCandidate = node;
        }
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        // Arrastar organiza pastas: só na visão por pastas.
        if (_dragging || _dragCandidate is null || e.LeftButton != MouseButtonState.Pressed || _sessions.IsReadOnly ||
            Grouping != TreeGrouping.Folders)
        {
            return;
        }

        var position = e.GetPosition(SessionTree);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        TreeDragData? data = _dragCandidate switch
        {
            SessionNode s when HasMultiSelection && _picked.Contains(s.Session.Id) =>
                new TreeDragData(null, null, $"{_picked.Count} sessões", PickedEntries().Select(p => p.Id).ToList()),
            SessionNode s => new TreeDragData(s.Session.Id, null, s.Name),
            FolderNode f => new TreeDragData(null, f.Path, f.Name),
            _ => null,
        };
        if (data is null)
        {
            return;
        }

        _dragging = true;
        _pendingSingle = null;
        var atRoot = data.SessionIds is { } many
            ? _sessions.Document.Sessions.Where(s => many.Contains(s.Id)).All(s => s.FolderPath.Length == 0)
            : data.SessionId is not null
                ? _dragCandidate is SessionNode { Session.FolderPath.Length: 0 }
                : FolderPaths.Parent(data.FolderPath!).Length == 0;
        RootDropZone.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            DragDrop.DoDragDrop(SessionTree, new DataObject(DragFormat, data), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _dragCandidate = null;
            _expandTimer?.Stop();
            SetDropHighlight(null, rootZone: false);
            RootDropZone.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ destino

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is not TreeDragData data)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        AutoScroll(e);
        var node = FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext as TreeNode;
        var target = TargetFolder(node);
        var allowed = CanDrop(data, target);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;

        TreeNode? highlight = target.Length == 0 ? null : _folderNodes.GetValueOrDefault(target);
        SetDropHighlight(allowed ? highlight : null, rootZone: allowed && target.Length == 0);

        if (node is FolderNode { IsExpanded: false } folder && allowed)
        {
            if (_expandCandidate != folder)
            {
                _expandCandidate = folder;
                _expandTimer?.Stop();
                _expandTimer?.Start();
            }
        }
        else
        {
            _expandCandidate = null;
            _expandTimer?.Stop();
        }
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave também dispara ao passar entre itens: só limpa se o mouse saiu da árvore.
        var p = e.GetPosition(SessionTree);
        if (p.X < 0 || p.Y < 0 || p.X > SessionTree.ActualWidth || p.Y > SessionTree.ActualHeight)
        {
            SetDropHighlight(null, rootZone: false);
        }
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is TreeDragData data)
        {
            var node = FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext as TreeNode;
            _ = ApplyMoveAsync(data, TargetFolder(node));
        }
    }

    private void OnRootZoneDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var allowed = e.Data.GetData(DragFormat) is TreeDragData data && CanDrop(data, "");
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHighlight(null, rootZone: allowed);
    }

    private void OnRootZoneDragLeave(object sender, DragEventArgs e) => SetDropHighlight(null, rootZone: false);

    private void OnRootZoneDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is TreeDragData data)
        {
            _ = ApplyMoveAsync(data, "");
        }
    }

    /// <summary>Pasta que recebe o item: a própria pasta, a pasta da sessão sob o mouse ou a raiz (área vazia).</summary>
    private static string TargetFolder(TreeNode? node) => node switch
    {
        FolderNode folder => folder.Path,
        SessionNode session => session.Session.FolderPath,
        _ => "",
    };

    private bool CanDrop(TreeDragData data, string target)
    {
        if (data.SessionIds is { } ids)
        {
            return ids.Any(id => _sessions.CheckMoveSession(id, target) == MoveCheck.Allowed);
        }

        if (data.SessionId is Guid id)
        {
            return _sessions.CheckMoveSession(id, target) == MoveCheck.Allowed;
        }

        return _sessions.CheckMoveFolder(data.FolderPath!, target) is MoveCheck.Allowed or MoveCheck.WouldMerge;
    }

    private void SetDropHighlight(TreeNode? node, bool rootZone)
    {
        if (_dropHighlight != node)
        {
            if (_dropHighlight is not null)
            {
                _dropHighlight.IsDropTarget = false;
            }

            _dropHighlight = node;
            if (node is not null)
            {
                node.IsDropTarget = true;
            }
        }

        RootDropZone.Background = rootZone ? (Brush)FindResource("RdAccentSoft") : Brushes.Transparent;
        RootDropZone.BorderBrush = (Brush)FindResource(rootZone ? "RdAccent" : "RdBorder");
    }

    private void AutoScroll(DragEventArgs e)
    {
        _treeScroll ??= FindDescendant<ScrollViewer>(SessionTree);
        if (_treeScroll is null)
        {
            return;
        }

        var y = e.GetPosition(SessionTree).Y;
        if (y < AutoScrollMargin)
        {
            _treeScroll.ScrollToVerticalOffset(_treeScroll.VerticalOffset - 10);
        }
        else if (y > SessionTree.ActualHeight - AutoScrollMargin)
        {
            _treeScroll.ScrollToVerticalOffset(_treeScroll.VerticalOffset + 10);
        }
    }

    // ------------------------------------------------------------------ execução

    private async Task ApplyMoveAsync(TreeDragData data, string target)
    {
        if (!EnsureWritable())
        {
            return;
        }

        string? message = null;
        Func<TreeNode, bool>? reselect = null;
        if (data.SessionIds is { } ids)
        {
            var moved = _sessions.MoveSessions(ids, target);
            if (moved > 0)
            {
                // A seleção continua marcada depois de mover (ApplyPicked na reconstrução).
                message = $"{moved} sessões movidas para {Describe(target)}.";
            }
        }
        else if (data.SessionId is Guid id)
        {
            if (_sessions.MoveSession(id, target))
            {
                message = $"\"{data.Name}\" movida para {Describe(target)}.";
                reselect = n => n is SessionNode s && s.Session.Id == id;
            }
        }
        else if (data.FolderPath is { } folder)
        {
            if (_sessions.CheckMoveFolder(folder, target) == MoveCheck.WouldMerge &&
                !await Prompts.ConfirmAsync(
                    "Mesclar pastas?",
                    $"Já existe uma pasta \"{data.Name}\" em {Describe(target)}.\n\nMover junta o conteúdo das duas. Você pode desfazer com Ctrl+Z.",
                    "Mesclar"))
            {
                return;
            }

            if (_sessions.MoveFolder(folder, target) is { } moved)
            {
                message = $"Pasta \"{data.Name}\" movida para {Describe(target)}.";
                reselect = n => n is FolderNode f && string.Equals(f.Path, moved, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (message is null)
        {
            return;
        }

        AfterReorganize(message + "  Ctrl+Z desfaz.", reselect);
    }

    private async Task RenameFolderAsync(FolderNode folder)
    {
        if (!EnsureWritable() || await Prompts.AskTextAsync("Renomear pasta", "Novo nome", folder.Name, "Renomear") is not { } name)
        {
            return;
        }

        var renamed = _sessions.RenameFolder(folder.Path, name, out var error);
        if (renamed is null)
        {
            await Prompts.InfoAsync("Não foi possível renomear", error ?? "Nome inválido.");
            return;
        }

        AfterReorganize($"Pasta renomeada para \"{name}\".  Ctrl+Z desfaz.",
            n => n is FolderNode f && string.Equals(f.Path, renamed, StringComparison.OrdinalIgnoreCase));
    }

    private void UndoOrganize()
    {
        var undone = _sessions.Undo();
        if (undone is null)
        {
            Notify("Nada para desfazer.", false);
            return;
        }

        AfterReorganize($"Desfeito: {undone}.", null);
    }

    /// <summary>Atualiza a árvore depois do arraste terminar e devolve a seleção ao item movido.</summary>
    private void AfterReorganize(string message, Func<TreeNode, bool>? reselect)
    {
        _settings.CollapsedFolders = _sessions.CollapsedFolders.ToList();
        SaveSettings();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            RebuildTree();
            if (reselect is not null && FindNode(SessionTree.ItemsSource as IEnumerable<TreeNode> ?? [], reselect) is { } node)
            {
                node.IsSelected = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BringIntoView(node));
            }

            Notify(message, false);
        });
    }

    // ------------------------------------------------------------------ menu "Mover para"

    private MenuItem MoveToMenu(Func<string, bool> canMove, Action<string> move)
    {
        var menu = new MenuItem { Header = "Mover para" };
        if (canMove(""))
        {
            menu.Items.Add(MenuItem("(raiz)", () => move("")));
        }

        foreach (var folder in _sessions.Document.AllFolders())
        {
            if (!canMove(folder))
            {
                continue;
            }

            var depth = folder.Count(c => c == '/');
            menu.Items.Add(MenuItem(new string(' ', depth * 4) + FolderPaths.LeafName(folder), () => move(folder)));
        }

        menu.IsEnabled = menu.Items.Count > 0;
        return menu;
    }

    private MenuItem MoveSessionMenu(SessionNode node) => MoveToMenu(
        target => _sessions.CheckMoveSession(node.Session.Id, target) == MoveCheck.Allowed,
        target => _ = ApplyMoveAsync(new TreeDragData(node.Session.Id, null, node.Name), target));

    private MenuItem MoveFolderMenu(FolderNode node) => MoveToMenu(
        target => _sessions.CheckMoveFolder(node.Path, target) is MoveCheck.Allowed or MoveCheck.WouldMerge,
        target => _ = ApplyMoveAsync(new TreeDragData(null, node.Path, node.Name), target));

    // ------------------------------------------------------------------ utilidades

    private static string Describe(string folder) => folder.Length == 0 ? "a raiz" : $"\"{folder.Replace("/", " / ")}\"";

    private static TreeNode? FindNode(IEnumerable<TreeNode> nodes, Func<TreeNode, bool> match)
    {
        foreach (var node in nodes)
        {
            if (match(node))
            {
                return node;
            }

            if (FindNode(node.Children, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void BringIntoView(TreeNode target) => FindContainer(target)?.BringIntoView();

    /// <summary>
    /// Item visual (TreeViewItem) de um nó. A árvore é virtualizada: um item fora da tela não existe
    /// até rolar até ele, então o caminho é percorrido abrindo as pastas e pedindo ao painel de cada
    /// nível que crie o item (BringIndexIntoViewPublic).
    /// </summary>
    private TreeViewItem? FindContainer(object? target)
    {
        if (target is not TreeNode node || PathTo(node) is not { } path)
        {
            return null;
        }

        ItemsControl parent = SessionTree;
        TreeViewItem? container = null;
        foreach (var step in path)
        {
            var index = parent.Items.IndexOf(step);
            if (index < 0)
            {
                return null;
            }

            container = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            if (container is null)
            {
                parent.ApplyTemplate();
                parent.UpdateLayout();
                if (FindItemsHost(parent) is { } panel)
                {
                    panel.BringIndexIntoViewPublic(index);
                    container = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
                }
            }

            if (container is null)
            {
                return null;
            }

            if (!ReferenceEquals(step, node))
            {
                container.IsExpanded = true;
            }

            parent = container;
        }

        return container;
    }

    /// <summary>Nós da raiz até <paramref name="target"/> (inclusive), ou null se não está na árvore atual.</summary>
    private List<TreeNode>? PathTo(TreeNode target)
    {
        bool Walk(IEnumerable<TreeNode> level, List<TreeNode> path)
        {
            foreach (var item in level)
            {
                path.Add(item);
                if (ReferenceEquals(item, target) || (item is FolderNode folder && Walk(folder.Children, path)))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        var result = new List<TreeNode>();
        return SessionTree.ItemsSource is IEnumerable<TreeNode> roots && Walk(roots, result) ? result : null;
    }

    private static VirtualizingStackPanel? FindItemsHost(ItemsControl owner)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(owner);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is VirtualizingStackPanel { IsItemsHost: true } panel && ItemsControl.GetItemsOwner(panel) == owner)
                {
                    return panel;
                }

                if (child is not TreeViewItem) // não desce nos itens filhos (cada um tem o seu painel)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(object source)
        where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null; current = Parent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;

        // Trechos de texto (Run) não fazem parte da árvore visual: sobe pela árvore lógica.
        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
