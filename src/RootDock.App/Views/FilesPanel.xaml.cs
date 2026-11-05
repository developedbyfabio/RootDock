using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public FilesPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
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
                Clipboard.SetText(string.Join(Environment.NewLine, Selected.Select(s => s.Entry.FullPath)));
                break;
            case Key.V when ctrl && Clipboard.ContainsFileDropList():
                e.Handled = true;
                await UploadAsync(Clipboard.GetFileDropList().Cast<string>().ToList(), vm.CurrentPath);
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
            menu.Items.Add(Menu("Copiar caminho", () => Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(s => s.Entry.FullPath))), "Ctrl+C"));
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
