using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.Core.Sftp;

namespace RootDock.App.Views;

/// <summary>Painel lateral de arquivos do servidor ativo (SFTP).</summary>
public partial class FilesPanel : UserControl
{
    private static string _lastDownloadFolder = DownloadsFolder();

    /// <summary>Espaço das linhas fora das colunas (margem interna do item, dos dois lados) mais 1 px de folga.</summary>
    private const double RowChrome = 13;

    /// <summary>Largura mínima do Nome ao alargar o painel para ver todas as colunas.</summary>
    private const double WideNameWidth = 260;

    private ScrollViewer? _scroller;

    public FilesPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
        List.Loaded += (_, _) => HookScroller();
    }

    /// <summary>Larguras e colunas visíveis (a janela liga às preferências salvas).</summary>
    public FileColumnsLayout Columns => (FileColumnsLayout)Resources["Columns"];

    /// <summary>Menu de colunas → alargar/voltar a barra lateral (recebe a largura que mostra todas as colunas).</summary>
    public event Action<double>? WidenRequested;

    /// <summary>A barra lateral está alargada pelo menu de colunas (o item passa a ser "voltar").</summary>
    public Func<bool>? IsWidened { get; set; }

    private void OnSortHeader(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: string tag } && Enum.TryParse<FileSortColumn>(tag, out var column))
        {
            vm.SortBy(column);
            Columns.Settings.SortColumn = vm.SortColumn.ToString();
            Columns.Settings.SortDescending = vm.SortDescending;
            Columns.Save();
        }
    }

    // ------------------------------------------------------------------ cabeçalho e colunas

    private void HookScroller()
    {
        if (_scroller is not null || FindChild<ScrollViewer>(List) is not { } scroller)
        {
            return;
        }

        _scroller = scroller;
        scroller.ScrollChanged += (_, e) =>
        {
            if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
            {
                SyncHeader();
            }
        };
        scroller.SizeChanged += (_, _) => SyncHeader();
        SyncHeader();
    }

    /// <summary>
    /// Cabeçalho alinhado com as linhas: mesma origem, mesma área útil (a barra de rolagem vertical
    /// aparece e some) e a mesma rolagem horizontal. O Nome automático ocupa o espaço que sobra.
    /// </summary>
    private void SyncHeader()
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        if (FindChild<ItemsPresenter>(List) is { } items && FindChild<ScrollContentPresenter>(scroller) is { ActualWidth: > 0 } viewport)
        {
            var left = items.TranslatePoint(default, ListArea).X;
            var right = ListArea.ActualWidth - viewport.TranslatePoint(new Point(viewport.ActualWidth, 0), ListArea).X;
            var margin = new Thickness(Math.Max(0, Math.Round(left)), 2, Math.Max(0, Math.Round(right)), 2);
            if (margin != HeaderHost.Margin)
            {
                HeaderHost.Margin = margin;
            }
        }

        if (scroller.ViewportWidth > 0)
        {
            Columns.SetAvailable(scroller.ViewportWidth - RowChrome);
        }

        HeaderScroller.ScrollToHorizontalOffset(scroller.HorizontalOffset);
    }

    /// <summary>Shift + roda do mouse rola para os lados (as colunas podem passar da largura do painel).</summary>
    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift && _scroller is { ScrollableWidth: > 0 } scroller)
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta / 2.5);
            e.Handled = true;
        }
    }

    private double _gripStartWidth;
    private double _gripStartName;
    private double _gripStartX;
    private bool _gripMoved;

    private static FileColumn? ColumnOf(object sender) =>
        sender is FrameworkElement { Tag: string tag } && Enum.TryParse<FileColumn>(tag, out var column) ? column : null;

    private void OnGripStarted(object sender, DragStartedEventArgs e)
    {
        if (ColumnOf(sender) is not { } column)
        {
            return;
        }

        _gripStartWidth = Columns.WidthOf(column);
        _gripStartName = Columns.WidthOf(FileColumn.Name);
        _gripStartX = Mouse.GetPosition(HeaderHost).X;
        _gripMoved = false;
    }

    /// <summary>
    /// Largura = largura inicial + quanto o mouse andou (posição absoluta, não a soma dos passos:
    /// não acumula erro quando a coluna bate no mínimo). A divisória acompanha o mouse.
    /// </summary>
    private void OnGripDelta(object sender, DragDeltaEventArgs e)
    {
        if (ColumnOf(sender) is not { } column)
        {
            return;
        }

        var delta = Mouse.GetPosition(HeaderHost).X - _gripStartX;
        if (!_gripMoved && Math.Abs(delta) < 1)
        {
            return;
        }

        if (!_gripMoved && column != FileColumn.Name && Columns.IsNameAuto)
        {
            // Nome automático encolheria para compensar e a divisória não sairia do lugar:
            // ao mexer numa coluna, o Nome fica com a largura que tinha.
            Columns.SetWidth(FileColumn.Name, _gripStartName);
        }

        _gripMoved = true;
        Columns.SetWidth(column, _gripStartWidth + delta);
    }

    private void OnGripCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_gripMoved)
        {
            Columns.Save();
        }

        _gripMoved = false;
    }

    /// <summary>Dois cliques na divisória: a coluna fica do tamanho do maior texto (como no Explorer).</summary>
    private void OnGripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ColumnOf(sender) is not { } column)
        {
            return;
        }

        Columns.SetWidth(column, FitWidth(column));
        Columns.Save();
        e.Handled = true;
    }

    private void OnHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        ShowColumnsMenu(HeaderHost, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void OnColumnsButton(object sender, RoutedEventArgs e) => ShowColumnsMenu(DetailsButton, PlacementMode.Bottom);

    private void ShowColumnsMenu(UIElement target, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Items.Add(new MenuItem { Header = "Colunas visíveis", IsEnabled = false });
        foreach (var column in FileColumnSettings.Optional)
        {
            var item = new MenuItem { Header = ColumnLabel(column), IsCheckable = true, IsChecked = Columns.IsVisible(column), StaysOpenOnClick = true };
            // Checked/Unchecked (e não Click): também vale para teclado e leitor de tela.
            item.Checked += (_, _) => Columns.SetVisible(column, true);
            item.Unchecked += (_, _) => Columns.SetVisible(column, false);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Ajustar larguras ao conteúdo", FitAll));
        menu.Items.Add(Menu("Restaurar colunas e larguras", Columns.Reset));
        if (WidenRequested is not null)
        {
            menu.Items.Add(new Separator());
            var widened = IsWidened?.Invoke() == true;
            menu.Items.Add(Menu(widened ? "Voltar o painel à largura anterior" : "Alargar o painel para ver todas as colunas",
                () => WidenRequested?.Invoke(WidthForAllColumns())));
        }

        menu.IsOpen = true;
    }

    /// <summary>Colunas do tamanho do conteúdo; o Nome volta a ocupar o espaço que sobra.</summary>
    private void FitAll()
    {
        foreach (var column in FileColumnSettings.Optional.Where(Columns.IsVisible))
        {
            Columns.SetWidth(column, FitWidth(column));
        }

        Columns.AutoSizeName();
        Columns.Save();
    }

    /// <summary>Largura do painel que mostra todas as colunas visíveis sem rolar para o lado.</summary>
    private double WidthForAllColumns()
    {
        var name = Columns.IsNameAuto ? WideNameWidth : Math.Max(Columns.WidthOf(FileColumn.Name), FileColumnSettings.MinNameWidth);
        var chrome = _scroller is { ViewportWidth: > 0 } s ? ActualWidth - s.ViewportWidth : 30;
        return Math.Ceiling(name + Columns.Settings.OthersWidth + RowChrome + chrome + 2);
    }

    private static string ColumnLabel(FileColumn column) => column switch
    {
        FileColumn.Name => "Nome",
        FileColumn.Size => "Tamanho",
        FileColumn.Modified => "Modificado",
        FileColumn.Owner => "Dono",
        FileColumn.Group => "Grupo",
        _ => "Permissões",
    };

    /// <summary>Largura que mostra o maior texto da coluna (e o título) sem cortar.</summary>
    private double FitWidth(FileColumn column)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var normal = new Typeface(List.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typeface = column == FileColumn.Permissions
            ? new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            : normal;
        var size = column switch
        {
            FileColumn.Name => 13,
            FileColumn.Permissions => 11,
            _ => 11.5,
        };

        // Linhas repetem muito (datas, donos): mede cada texto uma vez; pastas enormes, só as primeiras.
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Vm?.Entries.Take(5000) ?? [])
        {
            texts.Add(column switch
            {
                FileColumn.Name => entry.Name,
                FileColumn.Size => entry.SizeText,
                FileColumn.Modified => entry.ModifiedText,
                FileColumn.Owner => entry.Owner,
                FileColumn.Group => entry.Group,
                _ => entry.Permissions,
            });
        }

        var content = texts.Count == 0 ? 0 : texts.Max(t => Measure(t, typeface, size, dpi));
        var title = Measure(ColumnLabel(column), normal, 11.5, dpi) + 9; // + seta da ordem
        var chrome = column switch
        {
            FileColumn.Name => 24 + 8, // ícone e espaço; margem direita
            FileColumn.Size => 6 + 10,
            _ => 8 + 6,
        };
        return Math.Ceiling(Math.Max(content + chrome, title + 14) + 2);
    }

    private static double Measure(string text, Typeface typeface, double size, double dpi) =>
        string.IsNullOrEmpty(text) ? 0 :
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, dpi)
            .WidthIncludingTrailingWhitespace;

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public RemoteEditService? Editor { get; set; }

    /// <summary>Editor próprio (abas no RootDock).</summary>
    public EditorService? TextEditor { get; set; }

    /// <summary>Abre o gerenciador em duas colunas para estes arquivos.</summary>
    public Action<RemoteFilesViewModel>? OpenManager { get; set; }

    private void OnOpenManager(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            OpenManager?.Invoke(vm);
        }
    }

    /// <summary>Tema trocado (claro/escuro): redesenha os ícones da lista (variantes do tema claro).</summary>
    public void RefreshIcons() => List.Items.Refresh();

    private Rdp.RemoteDesktopInfo? _remoteDesktop;

    /// <summary>Área de Trabalho Remota ativa (sem terminal SSH): mostra o acesso aos arquivos dela.</summary>
    public Rdp.RemoteDesktopInfo? RemoteDesktop
    {
        get => _remoteDesktop;
        set
        {
            _remoteDesktop = value;
            if (value is not null)
            {
                RdpCardTitle.Text = string.Equals(value.Name, value.Host, StringComparison.OrdinalIgnoreCase)
                    ? $"Servidor Windows {value.Host}"
                    : $"{value.Name}\n{value.Host}";
            }

            UpdatePlaceholder();
        }
    }

    /// <summary>Abre o gerenciador em duas colunas para a Área de Trabalho Remota (id da aba).</summary>
    public Action<string>? OpenRemoteDesktopFiles { get; set; }

    private void OnOpenRdpFiles(object sender, RoutedEventArgs e)
    {
        if (_remoteDesktop is { } rdp)
        {
            OpenRemoteDesktopFiles?.Invoke(rdp.Id);
        }
    }

    public TransferQueue? Transfers
    {
        get => TransfersPanel.DataContext as TransferQueue;
        set => TransfersPanel.DataContext = value;
    }

    private RemoteFilesViewModel? Vm => DataContext as RemoteFilesViewModel;

    private IReadOnlyList<RemoteEntryViewModel> Selected => List.SelectedItems.Cast<RemoteEntryViewModel>().ToList();

    /// <summary>Painel visível: abre o SFTP se ainda não estiver aberto.</summary>
    public void Activate()
    {
        // Erro não reabre sozinho (o usuário usa "Tentar de novo"); sessão caída espera a reconexão.
        if (Vm is { State: FilesState.Idle, IsSessionDisconnected: false } vm)
        {
            _ = vm.EnsureConnectedAsync();
        }
    }

    private void UpdatePlaceholder()
    {
        var has = Vm is not null;
        var rdp = !has && _remoteDesktop is not null;
        Placeholder.Visibility = has || rdp ? Visibility.Collapsed : Visibility.Visible;
        RdpCard.Visibility = rdp ? Visibility.Visible : Visibility.Collapsed;
        ContentRoot.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has && IsVisible)
        {
            Activate();
        }
    }

    // ------------------------------------------------------------------ navegação

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.UpAsync();
        }
    }

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.HomeAsync();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.RefreshAsync();
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.EnsureConnectedAsync();
        }
    }

    private async void OnPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            e.Handled = true;
            await vm.NavigateAsync(vm.PathInput);
            List.Focus();
        }
        else if (e.Key == Key.Escape && Vm is { } vm2)
        {
            vm2.PathInput = vm2.CurrentPath;
            List.Focus();
        }
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: RemoteEntryViewModel item })
        {
            return;
        }

        e.Handled = true;
        await OpenAsync(item);
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Enter when List.SelectedItem is RemoteEntryViewModel item:
                e.Handled = true;
                await OpenAsync(item);
                break;
            case Key.Back:
                e.Handled = true;
                await vm.UpAsync();
                break;
            case Key.F5:
                e.Handled = true;
                await vm.RefreshAsync();
                break;
            case Key.F2 when List.SelectedItem is RemoteEntryViewModel item:
                e.Handled = true;
                await RenameAsync(item);
                break;
            case Key.Delete when Selected.Count > 0:
                e.Handled = true;
                await DeleteAsync(Selected);
                break;
            case Key.C when ctrl && Selected.Count > 0:
                e.Handled = true;
                SafeClipboard.SetText(string.Join(Environment.NewLine, Selected.Select(s => s.Entry.FullPath)));
                break;
            case Key.V when ctrl && SafeClipboard.GetFileDropList() is { Count: > 0 } files:
                e.Handled = true;
                await UploadAsync(files.ToList(), vm.CurrentPath);
                break;
        }
    }

    private bool _opening;

    /// <summary>Pasta: entra. Arquivo: abre no editor do RootDock (binário ou grande: oferece o programa padrão).</summary>
    private async Task OpenAsync(RemoteEntryViewModel item)
    {
        if (Vm is not { } vm || _opening || await vm.TryEnterAsync(item))
        {
            return;
        }

        if (TextEditor is null)
        {
            await OpenExternalAsync(item);
            return;
        }

        // Num link lento o arquivo demora a chegar: o círculo de progresso mostra que está abrindo
        // (e um segundo duplo clique não abre duas abas).
        _opening = true;
        vm.IsBusy = true;
        try
        {
            await TextEditor.OpenAsync(vm, item.Entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException && Editor is not null)
        {
            Log.Info($"Editor: {item.Entry.FullPath} não abre no editor ({ex.Message})");
            vm.IsBusy = false;
            if (await Prompts.ConfirmAsync("Abrir no programa padrão?", ex.Message, "Abrir no programa padrão"))
            {
                await OpenExternalAsync(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Abrir {item.Entry.FullPath}", ex);
            vm.IsBusy = false;
            await Prompts.InfoAsync("Não foi possível abrir", ex.Message);
        }
        finally
        {
            vm.IsBusy = false;
            _opening = false;
        }
    }

    /// <summary>VS Code (se instalado) ou o programa padrão do Windows, reenviando a cada salvamento.</summary>
    private async Task OpenExternalAsync(RemoteEntryViewModel item)
    {
        if (Vm is not { } vm || Editor is null)
        {
            return;
        }

        try
        {
            await Editor.OpenAsync(vm, item.Entry);
        }
        catch (Exception ex)
        {
            Log.Error($"Abrir {item.Entry.FullPath}", ex);
            await Prompts.InfoAsync("Não foi possível abrir", ex.Message);
        }
    }

    // ------------------------------------------------------------------ menu de contexto

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var item = ItemUnder(e.OriginalSource);
        if (item is not null && !List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(item);
        }

        var selected = item is null ? [] : Selected;
        var menu = new ContextMenu();
        if (selected.Count == 1)
        {
            var one = selected[0];
            if (one.IsDirectory)
            {
                menu.Items.Add(Menu("Abrir pasta", () => _ = OpenAsync(one), "Enter"));
                menu.Items.Add(Menu("Ir para esta pasta no terminal", () => vm.ChangeTerminalDirectory(one.Entry.FullPath)));
            }
            else
            {
                var edit = Menu("Editar no RootDock", () => _ = OpenAsync(one), "Enter");
                edit.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(edit);
                menu.Items.Add(Menu(RemoteEditService.VsCodePath is null ? "Abrir no programa padrão" : "Abrir no VS Code", () => _ = OpenExternalAsync(one)));
                menu.Items.Add(Menu("Acompanhar no terminal (tail -f)", () => vm.FollowInTerminal(one.Entry.FullPath)));
            }

            menu.Items.Add(new Separator());
        }

        if (selected.Count > 0)
        {
            menu.Items.Add(Menu("Baixar para Downloads", () => Download(selected, DownloadsFolder())));
            menu.Items.Add(Menu("Baixar para...", () => DownloadTo(selected)));
            menu.Items.Add(Menu("Copiar caminho", () => SafeClipboard.SetText(string.Join(Environment.NewLine, selected.Select(s => s.Entry.FullPath))), "Ctrl+C"));
            if (selected.Count == 1)
            {
                menu.Items.Add(Menu("Renomear...", () => _ = RenameAsync(selected[0]), "F2"));
                if (!selected[0].IsDirectory)
                {
                    menu.Items.Add(Menu("Permissões...", () => _ = ChmodAsync(selected[0])));
                }
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Menu(selected.Count == 1 ? "Excluir" : $"Excluir {selected.Count} itens", () => _ = DeleteAsync(selected), "Del"));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Menu("Enviar arquivos aqui...", () => OnUpload(this, new RoutedEventArgs())));
        menu.Items.Add(Menu("Nova pasta...", () => OnNewFolder(this, new RoutedEventArgs())));
        menu.Items.Add(Menu("Novo arquivo...", () => OnNewFile(this, new RoutedEventArgs())));
        menu.Items.Add(Menu("Atualizar", () => _ = vm.RefreshAsync(), "F5"));

        menu.PlacementTarget = List;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Menu(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    // ------------------------------------------------------------------ operações

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await Prompts.AskTextAsync("Nova pasta", "Nome da pasta", primary: "Criar") is { } name)
        {
            await vm.CreateFolderAsync(name);
        }
    }

    private async void OnNewFile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await Prompts.AskTextAsync("Novo arquivo", "Nome do arquivo", primary: "Criar") is { } name)
        {
            await vm.CreateFileAsync(name);
        }
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = $"Enviar para {vm.CurrentPath}", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await UploadAsync(dialog.FileNames, vm.CurrentPath);
        }
    }

    private void OnDownloadToDownloads(object sender, RoutedEventArgs e)
    {
        if (Selected.Count > 0)
        {
            Download(Selected, DownloadsFolder());
        }
    }

    private void OnCdHere(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && !string.IsNullOrEmpty(vm.CurrentPath))
        {
            vm.ChangeTerminalDirectory(vm.CurrentPath);
        }
    }

    private async Task UploadAsync(IReadOnlyList<string> paths, string target)
    {
        if (Vm is not { } vm || paths.Count == 0)
        {
            return;
        }

        var existing = vm.ExistingNames(paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))), target);
        if (existing.Count > 0 &&
            !await Prompts.ConfirmAsync("Substituir no servidor?",
                $"{existing.Count} item(ns) já existe(m) em {target}:\n\n{string.Join("\n", existing.Take(8))}{(existing.Count > 8 ? "\n..." : "")}\n\nSubstituir?",
                "Substituir", danger: true))
        {
            return;
        }

        if (vm.IsProduction &&
            !await Prompts.ConfirmAsync("Enviar para PRODUÇÃO?", $"Enviar {paths.Count} item(ns) para {target} em {vm.Session.Name}?", "Enviar", danger: true))
        {
            return;
        }

        vm.Upload(paths, target);
    }

    private void Download(IReadOnlyList<RemoteEntryViewModel> items, string folder)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        vm.Download(items, folder);
        _lastDownloadFolder = folder;
    }

    private void DownloadTo(IReadOnlyList<RemoteEntryViewModel> items)
    {
        var dialog = new OpenFolderDialog { Title = "Baixar para", InitialDirectory = _lastDownloadFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Download(items, dialog.FolderName);
        }
    }

    private async Task RenameAsync(RemoteEntryViewModel item)
    {
        if (Vm is { } vm && await Prompts.AskTextAsync("Renomear", "Novo nome", item.Name, "Renomear") is { } name && name != item.Name)
        {
            if (name.Contains('/'))
            {
                await Prompts.InfoAsync("Nome inválido", "O nome não pode conter \"/\".");
                return;
            }

            await vm.RenameAsync(item, name);
        }
    }

    private async Task DeleteAsync(IReadOnlyList<RemoteEntryViewModel> items)
    {
        if (Vm is not { } vm || items.Count == 0)
        {
            return;
        }

        var list = string.Join("\n", items.Take(8).Select(i => (i.IsDirectory ? "📁 " : "") + i.Entry.FullPath)) + (items.Count > 8 ? "\n..." : "");
        var hasFolders = items.Any(i => i.IsDirectory && !i.Entry.IsSymlink);
        var text = $"Excluir do servidor {vm.Session.Name}:\n\n{list}\n\n" +
                   (hasFolders ? "Pastas são apagadas com todo o conteúdo. " : "") +
                   "Não há lixeira: não dá para desfazer." +
                   (vm.IsProduction ? "\n\nATENÇÃO: este é um servidor de PRODUÇÃO." : "");
        if (await Prompts.ConfirmAsync("Excluir permanentemente?", text, "Excluir", danger: true))
        {
            await vm.DeleteAsync(items);
        }
    }

    private async Task ChmodAsync(RemoteEntryViewModel item)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var current = ToOctal(item.Entry.Permissions);
        var answer = await Prompts.AskTextAsync($"Permissões de {item.Name}", "ex.: 644, 755", current, "Aplicar");
        if (answer is null || answer == current)
        {
            return;
        }

        if (answer.Length is < 3 or > 4 || !answer.All(c => c is >= '0' and <= '7'))
        {
            await Prompts.InfoAsync("Permissão inválida", "Use 3 ou 4 dígitos octais, como 644 ou 0755.");
            return;
        }

        try
        {
            var fs = await vm.GetFileSystemAsync();
            await fs.ChangePermissionsAsync(item.Entry.FullPath, Convert.ToInt16(answer, 8), CancellationToken.None);
            await vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            await Prompts.InfoAsync("Não foi possível alterar", ex.Message);
        }
    }

    private static string ToOctal(string permissions)
    {
        if (permissions.Length < 10)
        {
            return "644";
        }

        int Triplet(int start) =>
            (permissions[start] == 'r' ? 4 : 0) + (permissions[start + 1] == 'w' ? 2 : 0) + (permissions[start + 2] == 'x' ? 1 : 0);

        return $"{Triplet(1)}{Triplet(4)}{Triplet(7)}";
    }

    // ------------------------------------------------------------------ arrastar para o Windows

    private Point _dragStart;
    private RemoteEntryViewModel? _dragItem;
    private bool _deferSelect;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ItemUnder(e.OriginalSource);
        _dragStart = e.GetPosition(List);
        _deferSelect = false;
        if (_dragItem is not null && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None &&
            List.SelectedItems.Count > 1 && List.SelectedItems.Contains(_dragItem))
        {
            // Como no Explorer: clicar num item de uma seleção múltipla não desfaz a seleção na
            // hora (pode ser o começo de um arrastar); sem arrastar, seleciona só ele ao soltar.
            _deferSelect = true;
            List.Focus();
            e.Handled = true;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _dragItem is { } item)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
            (List.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
        }

        _deferSelect = false;
        _dragItem = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is not { } item)
        {
            return;
        }

        var delta = e.GetPosition(List) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragItem = null;
        _deferSelect = false;
        if (!List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }

        DragToWindows(Selected);
    }

    /// <summary>
    /// Arrasta para o Explorer (ou a área de trabalho): o Windows escolhe a pasta, pergunta se
    /// substitui e mostra o progresso; o conteúdo vem do servidor enquanto é gravado.
    /// </summary>
    private void DragToWindows(IReadOnlyList<RemoteEntryViewModel> items)
    {
        if (Vm is not { IsReady: true } vm || items.Count == 0 || vm.DragListFor([.. items.Select(i => i.Entry)]) is not { } load)
        {
            return;
        }

        var dispatcher = Dispatcher;
        var data = new RootDock.Core.Shell.VirtualFileDataObject(load);
        data.Finished += result => dispatcher.BeginInvoke(() => ReportDrag(vm, result));
        var label = items.Count == 1 ? items[0].Name : $"{items.Count} itens";
        var image = ShellDragImage.Create(items[0].IconImage, label, this);
        Mouse.Capture(null); // o arrastar do Windows assume o mouse
        try
        {
            RootDock.Core.Shell.ShellDrag.DoDragDrop(data, image);
        }
        catch (Exception ex)
        {
            Log.Error("Arrastar para o Windows", ex);
            vm.Notify($"Não foi possível arrastar: {ex.Message}", true);
        }
    }

    private static void ReportDrag(RemoteFilesViewModel vm, RootDock.Core.Shell.VirtualFileDropResult result)
    {
        if (result.Error is { } error)
        {
            Log.Info($"Arrastar para o Windows: {error}");
            vm.Notify($"Cópia para o Windows com erro: {error}", true);
        }
        else if (result.Warning is { } warning)
        {
            vm.Notify($"Copiado para o Windows, mas {warning}.", true);
        }
        else if (result.Dropped && result.FilesCompleted > 0)
        {
            var files = result.FilesCompleted == 1 ? "1 arquivo copiado" : $"{result.FilesCompleted} arquivos copiados";
            vm.Notify($"{files} para o Windows ({Format.Bytes(result.Bytes)}).", false);
        }
    }

    // ------------------------------------------------------------------ arrastar do Windows

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Vm is { IsReady: true } && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { IsReady: true } vm || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        // Soltar sobre uma pasta envia para dentro dela.
        var target = ItemUnder(e.OriginalSource) is { IsDirectory: true } folder ? folder.Entry.FullPath : vm.CurrentPath;
        await UploadAsync(paths, target);
    }

    // ------------------------------------------------------------------ utilidades

    private static RemoteEntryViewModel? ItemUnder(object source)
    {
        // Texto (Run) não é visual: sobe pela árvore lógica até chegar a um elemento visual.
        static DependencyObject? Parent(DependencyObject d) =>
            d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        for (var current = source as DependencyObject; current is not null; current = Parent(current))
        {
            if (current is ListBoxItem { DataContext: RemoteEntryViewModel item })
            {
                return item;
            }
        }

        return null;
    }

    private static string DownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
