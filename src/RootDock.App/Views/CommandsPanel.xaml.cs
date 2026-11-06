using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RootDock.App.ViewModels;

namespace RootDock.App.Views;

/// <summary>Painel lateral direito: biblioteca de comandos para o terminal ativo.</summary>
public partial class CommandsPanel : UserControl
{
    public CommandsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CommandsViewModel old)
            {
                old.PropertyChanged -= OnVmChanged;
            }

            if (e.NewValue is CommandsViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
            }
        };
    }

    // Resultados novos: a lista volta ao topo (o primeiro resultado é o que o Enter usa).
    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandsViewModel.Results) && ResultsList.Template?.FindName("ResultsScroller", ResultsList) is ScrollViewer scroller)
        {
            scroller.ScrollToTop();
        }
    }

    public event Action? CloseRequested;

    public event Action? AddRequested;

    public event Action<CommandItemViewModel>? EditRequested;

    public event Action<CommandItemViewModel>? RemoveRequested;

    public event Action<string>? Copied;

    private CommandsViewModel? Vm => DataContext as CommandsViewModel;

    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnAdd(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.Search = "";
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        // A busca espera a pessoa parar de digitar (Delay): com Enter logo depois da última letra, busca já.
        SearchBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        if (vm.IsSearching && vm.Results.FirstOrDefault() is { } first)
        {
            // Enter na busca usa o primeiro resultado.
            e.Handled = true;
            Activate(first);
        }
    }

    private void OnCategoryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandCategoryViewModel category })
        {
            Vm?.SetExpanded(category, !category.IsExpanded);
        }
    }

    private void OnCommandClick(object sender, MouseButtonEventArgs e)
    {
        // Cliques no formulário (campos, botões) não executam o comando.
        if (IsInside(e.OriginalSource, "form") || FindAncestor<ButtonBase>(e.OriginalSource) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: CommandItemViewModel item })
        {
            e.Handled = true;
            Activate(item);
        }
    }

    private void Activate(CommandItemViewModel item)
    {
        var wasOpen = item.IsFormOpen;
        Vm?.Activate(item);
        if (!wasOpen && item.IsFormOpen)
        {
            FocusFirstField(item);
        }
    }

    private void OnCommandRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommandItemViewModel item } element)
        {
            return;
        }

        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(Menu(item.IsMacro ? "Executar a macro" : item.HasParams ? "Preencher e executar" : "Executar", () => Activate(item)));
        if (item.CanPaste)
        {
            menu.Items.Add(Menu("Colocar no terminal sem executar", () => Vm?.Send(item, CommandSendMode.Paste)));
        }

        menu.Items.Add(Menu(item.IsMacro ? "Copiar os comandos" : "Copiar", () => Copy(item)));
        menu.Items.Add(Menu(item.IsFavorite ? "Tirar dos favoritos" : "Favoritar", () => Vm?.ToggleFavorite(item)));
        if (item.IsCustom || item.IsMacro)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu(item.IsMacro ? "Editar a macro..." : "Editar...", () => EditRequested?.Invoke(item)));
            menu.Items.Add(Menu("Excluir", () => RemoveRequested?.Invoke(item)));
        }

        menu.IsOpen = true;
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            if (item.HasParams && !item.IsFormOpen)
            {
                Activate(item);
                return;
            }

            Vm?.Send(item, CommandSendMode.Run);
        }
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            if (item.HasParams && !item.IsFormOpen)
            {
                Activate(item);
                return;
            }

            Vm?.Send(item, CommandSendMode.Paste);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Copy(item);
        }
    }

    private void OnFavorite(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Vm?.ToggleFavorite(item);
        }
    }

    private void OnCancelForm(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            item.IsFormOpen = false;
            item.Error = "";
        }
    }

    private void OnParamKey(object sender, KeyEventArgs e)
    {
        // O campo pertence a um parâmetro: sobe até o cartão para achar o comando.
        if (FindAncestor<FrameworkElement>(sender, f => f.DataContext is CommandItemViewModel)?.DataContext is not CommandItemViewModel item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.Send(item, CommandSendMode.Run);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.IsFormOpen = false;
        }
    }

    private void Copy(CommandItemViewModel item)
    {
        var text = item.CopyText ?? (item.IsKeys ? item.DisplayCommand : item.Render() ?? item.DisplayCommand);
        try
        {
            Clipboard.SetText(text);
            Copied?.Invoke(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Área de transferência ocupada por outro programa: o usuário pode tentar de novo.
        }
    }

    private void FocusFirstField(CommandItemViewModel item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var box = FindDescendant<Wpf.Ui.Controls.TextBox>(this, t => t.DataContext is CommandParamViewModel p && item.Params.Contains(p));
            box?.Focus();
            box?.SelectAll();
            box?.BringIntoView();
        });
    }

    private static CommandItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as CommandItemViewModel;

    private static MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool IsInside(object source, string tag) =>
        FindAncestor<FrameworkElement>(source, f => Equals(f.Tag, tag)) is not null;

    private static T? FindAncestor<T>(object source, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null; current = Parent(current))
        {
            if (current is T typed && (match is null || match(typed)))
            {
                return typed;
            }
        }

        return null;

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed))
            {
                return typed;
            }

            if (FindDescendant(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
