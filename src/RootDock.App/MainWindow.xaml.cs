using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Windows.Threading;
using RootDock.App.Dialogs;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.App.ViewModels;
using RootDock.Core;
using RootDock.Core.Import;
using RootDock.Core.LocalShell;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Settings;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RootDock.App;

public partial class MainWindow : FluentWindow
{
    private static readonly Regex QuickConnectPattern = new(
        @"^(?:(?<user>[^@\s]+)@)?(?<host>[^:\s@]+)(?::(?<port>\d{1,5}))?$", RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly SessionManager _sessions;
    private readonly CredentialVault _vault = CredentialVault.Default;
    private readonly SessionLogins _logins = new(CredentialVault.Default);
    private readonly TunnelService _tunnels;
    private readonly KnownHostsStore _knownHosts;
    private readonly TerminalWorkspace _workspace;
    private readonly TransferQueue _transfers;
    private readonly RemoteEditService _editor;
    private readonly DispatcherTimer _noticeTimer;
    private readonly HashSet<string> _autoSwitchedToFiles = [];
    private bool _closeConfirmed;

    /// <summary>Sessão a abrir ao iniciar (argumento --open).</summary>
    public string? StartupSessionName { get; init; }

    public MainWindow(AppPaths paths)
    {
        InitializeComponent();
        BuildMainMenu();
        _paths = paths;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        ApplyStartupTheme();
        _sessions = new SessionManager(new SessionStore(paths.SessionsFile), _settings.CollapsedFolders);
        _knownHosts = new KnownHostsStore(paths.KnownHostsFile, KnownHostsStore.DefaultOpenSshFiles());
        _workspace = new TerminalWorkspace(WebView);
        _workspace.Connector = new SessionConnector(this, _knownHosts, _vault, _sessions);
        _transfers = new TransferQueue(Dispatcher);
        _editor = new RemoteEditService(Dispatcher, (title, text) => Prompts.ConfirmAsync(title, text, "Confirmar"), Notify);
        _tunnels = new TunnelService(Dispatcher);
        _tunnels.Changed += () => UpdateTunnelsBadge(_workspace.ActiveSession);
        _workspace.Services = new RemoteServices(_transfers, _editor, Notify, _tunnels);
        FilesView.Editor = _editor;
        var editors = new EditorService(_workspace, (title, text) => Prompts.ConfirmAsync(title, text, "Salvar", danger: true), Notify);
        _workspace.Editors = editors;
        FilesView.TextEditor = editors;
        _transferHistory = new Core.History.TransferHistory(Path.Combine(paths.DataDirectory, "transfers.jsonl"));
        FilesView.OpenManager = vm => FileManagerWindow.ShowFor(this,
            FileManagerWindow.SftpSource(vm, () => _workspace.Sessions.FirstOrDefault(s => s.Files == vm)?.SshClient, editors),
            _transfers, _transferHistory);
        _rdp = new Rdp.RdpService(_workspace, WebView, RdpLayer, this, _vault, _sessions, Notify)
        {
            Transfers = _transfers,
            TransferHistory = _transferHistory,
        };
        FilesView.OpenRemoteDesktopFiles = id => _ = _rdp.OpenFilesAsync(id);
        _history = new Core.History.CommandHistory(Path.Combine(paths.DataDirectory, "history.jsonl"));
        _workspace.History = _history;
        _workspace.HistoryRequested += OpenHistory;
        _workspace.RestoreRequested += RestoreWorkspace;
        _workspace.RecordingsDirectory = Path.Combine(paths.DataDirectory, "recordings");
        _workspace.Rdp = _rdp;
        _vnc = new Vnc.VncService(_workspace, _vault, _sessions, Notify);
        _ftp = new FtpService(this, _vault, _sessions, _transfers, _transferHistory, Notify);
        _workspace.Vnc = _vnc;
        FilesView.Transfers = _transfers;
        _transfers.Completed += item =>
        {
            if (item.State == TransferState.Done)
            {
                Notify($"✓ {item.Name} {(item.IsUpload ? "enviado" : "baixado")}", false);
            }
            else if (item.State == TransferState.Failed)
            {
                Notify($"{item.Name}: {item.Detail}", true);
            }

            _transferHistory.Add(new Core.History.TransferRecord(
                DateTimeOffset.Now, item.IsUpload, item.Name, item.Server,
                item.State == TransferState.Done ? item.TotalBytes : item.Done,
                item.State switch { TransferState.Done => "Concluído", TransferState.Canceled => "Cancelado", _ => "Erro: " + item.Detail },
                (DateTime.Now - item.StartedAt).TotalSeconds));
        };
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeBar.Visibility = Visibility.Collapsed;
        };

        _workspace.ActiveSessionChanged += UpdateActiveStatus;
        _workspace.SessionsChanged += UpdateSummary;
        _workspace.NewLocalRequested += () => OpenLocalShell(_settings.DefaultLocalShell);
        _workspace.QuickConnectRequested += FocusQuickConnect;
        _workspace.FontSizeChanged += size =>
        {
            _settings.FontSize = size;
            SaveSettings();
        };

        InitTreeOrganization();
        GroupingBox.SelectedIndex = Grouping switch { TreeGrouping.Company => 1, TreeGrouping.Environment => 2, TreeGrouping.Tags => 3, _ => 0 };
        InitCommands();
        RestorePlacement();
        InitUpdates();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnWindowKeyDown;
    }

    // ------------------------------------------------------------------ inicialização

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSessions();
        RebuildTree();

        try
        {
            await _workspace.InitializeAsync(_paths.WebViewDirectory, _settings.FontSize);
            _workspace.Send(AppThemes.PageMessage(AppThemes.Get(_settings.AppTheme)));
            _workspace.Send(TerminalThemes.Message(_settings.Terminal, _settings.FontSize));
            OfferRestore();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            await ShowMessage("WebView2 não encontrado",
                "O RootDock usa o Microsoft Edge WebView2 para desenhar os terminais.\n" +
                "Instale o \"Evergreen Runtime\" em https://developer.microsoft.com/microsoft-edge/webview2/ e abra o RootDock de novo.");
            return;
        }

        if (_settings.CommandsPanelOpen)
        {
            SetCommandsPanel(true, focusSearch: false);
        }

        await OfferMobaXtermImportOnFirstRun();
        AnnounceUpdate();

        if (StartupSessionName is { Length: > 0 } name)
        {
            var entry = _sessions.Document.Sessions.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (entry is not null)
            {
                Connect(entry);
            }
            else
            {
                Notify($"Sessão \"{name}\" não encontrada.", true);
            }
        }
    }

    private void LoadSessions()
    {
        try
        {
            _sessions.Load();
        }
        catch (SessionStoreCorruptedException ex)
        {
            Log.Error("Carregar sessões", ex);
            _ = ShowMessage("Arquivo de sessões com problema", ex.Message + "\n\nO RootDock abriu sem sessões e não vai gravar nada até o arquivo ser corrigido.");
        }
    }

    private async Task OfferMobaXtermImportOnFirstRun()
    {
        if (_sessions.Document.Sessions.Count > 0 || _sessions.IsReadOnly)
        {
            return;
        }

        var ini = MobaXtermImporter.FindDefaultIniPath();
        if (ini is null)
        {
            return;
        }

        var preview = MobaXtermImporter.ImportFile(ini);
        if (preview.Sessions.Count == 0)
        {
            return;
        }

        var box = new MessageBox
        {
            Title = "Bem-vindo ao RootDock",
            Content = $"Encontrei {preview.Sessions.Count} sessões SSH do MobaXterm em {preview.Folders.Count} pastas.\n\nQuer importá-las agora? O MobaXterm não é alterado.",
            PrimaryButtonText = "Importar",
            CloseButtonText = "Agora não",
        };
        if (await box.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await ImportFrom(ini);
        }
    }

    // ------------------------------------------------------------------ árvore de sessões

    private void RebuildTree()
    {
        var nodes = _sessions.BuildTree(SearchBox.Text, (folder, expanded) =>
        {
            _sessions.SetCollapsed(folder.Path, !expanded);
            _settings.CollapsedFolders = _sessions.CollapsedFolders.ToList();
            SaveSettings();
        }, Grouping);
        IndexFolderNodes(nodes);
        SessionTree.ItemsSource = nodes;
        UpdateSummary();
        ApplyPicked();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RebuildTree();

    private TreeGrouping Grouping =>
        Enum.TryParse<TreeGrouping>(_settings.TreeGrouping, out var grouping) ? grouping : TreeGrouping.Folders;

    private void OnGroupingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupingBox.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == _settings.TreeGrouping)
        {
            return;
        }

        _settings.TreeGrouping = tag;
        SaveSettings();
        RebuildTree();
        if (Grouping != TreeGrouping.Folders)
        {
            Notify("Agrupado por " + Grouping switch { TreeGrouping.Company => "empresa e filial", TreeGrouping.Tags => "tag", _ => "ambiente" } +
                   ". Para organizar arrastando, volte para \"Pastas\".", false);
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionTree.SelectedItem is SessionNode node && IsOnSelectedItem(e.OriginalSource))
        {
            e.Handled = true;
            Connect(node.Session);
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleSelectionKey(e))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when SessionTree.SelectedItem is SessionNode node:
                e.Handled = true;
                Connect(node.Session);
                break;
            case Key.F2 when SessionTree.SelectedItem is SessionNode node:
                e.Handled = true;
                EditSession(node.Session);
                break;
            case Key.Delete when SessionTree.SelectedItem is SessionNode node:
                e.Handled = true;
                _ = DeleteSessionsAsync([node.Session]);
                break;
            case Key.F2 when SessionTree.SelectedItem is FolderNode { IsVirtual: false } folder:
                e.Handled = true;
                _ = RenameFolderAsync(folder);
                break;
            case Key.Delete when SessionTree.SelectedItem is FolderNode { IsVirtual: false } folder:
                e.Handled = true;
                _ = DeleteFolder(folder.Path);
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                UndoOrganize();
                break;
        }
    }

    private bool IsOnSelectedItem(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TreeViewItem item)
            {
                return item.IsSelected;
            }
        }

        return false;
    }

    private bool _menuOnEmptyArea;

    /// <summary>Botão direito: seleciona o item sob o mouse (o menu abre em OnTreeContextMenuOpening).</summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource);
        _menuOnEmptyArea = item is null;
        // Botão direito sobre uma das sessões marcadas: o menu vale para todas.
        _menuForPicked = item?.DataContext is SessionNode picked && HasMultiSelection && _picked.Contains(picked.Session.Id);
        if (item is not null && !_menuForPicked)
        {
            ClearPicked();
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>Menu de contexto da árvore: abre com o botão direito, a tecla Menu ou Shift+F10.</summary>
    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        object? target = _menuOnEmptyArea ? null : SessionTree.SelectedItem;
        var forPicked = HasMultiSelection && (_menuForPicked || (!_menuOnEmptyArea && e.CursorLeft < 0));
        _menuOnEmptyArea = false;
        _menuForPicked = false;

        var menu = new ContextMenu();
        if (forPicked)
        {
            AddSelectionMenu(menu);
            target = null;
        }

        switch (target)
        {
            case null when forPicked:
                break;
            case SessionNode node:
                menu.Items.Add(MenuItem("Conectar", () => Connect(node.Session), "Enter"));
                if (node.Session.Kind == SessionKind.Rdp && node.Session.Host.Length > 0)
                {
                    // Sem abrir a Área de Trabalho Remota: só os arquivos, lado a lado com os deste computador.
                    menu.Items.Add(MenuItem("Arquivos do servidor (lado a lado)", () => _ = _rdp.OpenFilesAsync(node.Session, known: null)));
                }

                menu.Items.Add(MenuItem("Editar...", () => EditSession(node.Session), "F2"));
                menu.Items.Add(MenuItem("Duplicar", () => DuplicateSession(node.Session)));
                menu.Items.Add(TagMenu([node.Session]));
                menu.Items.Add(MoveSessionMenu(node));
                if (node.Session.Kind is SessionKind.Ssh or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Ftp)
                {
                    menu.Items.Add(new Separator());
                    var count = node.Session.Logins.Count;
                    menu.Items.Add(MenuItem(count > 0 ? $"Logins salvos ({count})..." : "Logins salvos...", () => OpenLogins(node.Session)));
                }

                if (node.Session.Kind is SessionKind.Ssh or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Vnc && node.Session.Host.Length > 0)
                {
                    var host = node.Session.Host;
                    var net = new System.Windows.Controls.MenuItem { Header = "Diagnóstico de rede" };
                    net.Items.Add(MenuItem($"Ping em {host}", () => OpenNetworkTools("ping", host)));
                    net.Items.Add(MenuItem("Traceroute / MTR", () => OpenNetworkTools("mtr", host)));
                    net.Items.Add(MenuItem("Portas abertas", () => OpenNetworkTools("ports", host)));
                    net.Items.Add(MenuItem("DNS", () => OpenNetworkTools("dns", host)));
                    net.Items.Add(MenuItem("Certificado SSL", () => OpenNetworkTools("ssl", host)));
                    menu.Items.Add(net);
                }

                if (node.Session.Kind == SessionKind.Ssh)
                {
                    var tunnels = node.Session.Tunnels.Count;
                    menu.Items.Add(MenuItem(tunnels > 0 ? $"Túneis ({tunnels})..." : "Túneis (redirecionar portas)...", () => OpenTunnels(node.Session)));
                    menu.Items.Add(MenuItem("Instalar minha chave SSH (entrar sem senha)...", () => _ = InstallKeyAsync([node.Session])));
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItem("Excluir", () => _ = DeleteSessionsAsync([node.Session]), "Del"));
                if (CopiesOf(node.Session) is { Count: > 0 } copies)
                {
                    menu.Items.Add(MenuItem(
                        copies.Count == 1 ? "Excluir a cópia repetida desta sessão" : $"Excluir as {copies.Count} cópias repetidas desta sessão",
                        () => _ = DeleteSessionsAsync(copies)));
                }
                break;
            case FolderNode { IsVirtual: true } group:
                AddGroupMenu(menu, group);
                break;
            case FolderNode folder:
                menu.Items.Add(MenuItem("Nova sessão aqui...", () => NewSession(folder.Path)));
                menu.Items.Add(MenuItem("Nova subpasta...", () => _ = NewFolder(folder.Path)));
                menu.Items.Add(MenuItem("Renomear pasta...", () => _ = RenameFolderAsync(folder), "F2"));
                menu.Items.Add(MoveFolderMenu(folder));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItem("Excluir pasta vazia", () => _ = DeleteFolder(folder.Path), "Del"));
                break;
            default:
                menu.Items.Add(MenuItem("Nova sessão...", () => NewSession("")));
                menu.Items.Add(MenuItem("Nova pasta...", () => _ = NewFolder("")));
                if (_sessions.CanUndo)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MenuItem("Desfazer última organização", UndoOrganize, "Ctrl+Z"));
                }

                break;
        }

        menu.PlacementTarget = SessionTree;
        if (e.CursorLeft < 0 && FindContainer(SessionTree.SelectedItem) is { } container)
        {
            // Aberto pelo teclado: posiciona no item selecionado.
            menu.PlacementTarget = container;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }

        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, Action action, string? gesture = null)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    // ------------------------------------------------------------------ ações de sessão

    private readonly Rdp.RdpService _rdp;
    private readonly Vnc.VncService _vnc;
    private readonly FtpService _ftp;
    private readonly Core.History.CommandHistory _history;
    private readonly Core.History.TransferHistory _transferHistory;

    /// <summary>Abre a sessão: terminal (SSH, Telnet, Serial, local) ou Área de Trabalho Remota.</summary>
    private void Connect(SessionEntry entry)
    {
        if (entry.Kind == SessionKind.Rdp)
        {
            _rdp.Open(entry);
        }
        else if (entry.Kind == SessionKind.Vnc)
        {
            _vnc.Open(entry);
        }
        else if (entry.Kind == SessionKind.Ftp)
        {
            _ = _ftp.OpenAsync(entry);
        }
        else
        {
            _workspace.Open(entry);
        }
    }

    private void OnNewSession(object sender, RoutedEventArgs e)
    {
        var folder = SessionTree.SelectedItem switch
        {
            FolderNode { IsVirtual: true } => "",
            FolderNode f => f.Path,
            SessionNode s => s.Session.FolderPath,
            _ => "",
        };
        NewSession(folder);
    }

    private void NewSession(string folder)
    {
        if (!EnsureWritable())
        {
            return;
        }

        if (SessionEditorWindow.Edit(this, new SessionEntry { FolderPath = folder }, _sessions.Document.AllFolders(), _vault, isNew: true, _sessions.Document.Sessions) is not { } result)
        {
            return;
        }

        // Já existe uma sessão que conecta do mesmo jeito (servidor, usuário, porta, chave, ambiente,
        // comando inicial): usa ela em vez de criar uma cópia. Nome e pasta são só organização.
        var entry = _sessions.Document.Sessions.FirstOrDefault(s => SameConnection(s, result.Entry)) ?? result.Entry;
        if (entry == result.Entry)
        {
            _sessions.AddOrUpdate(entry);
            RebuildTree();
        }
        else
        {
            Notify($"Já existe a sessão \"{entry.Name}\" com o mesmo servidor e usuário; ela foi usada em vez de criar outra.", false);
            if (FindNode(TreeNodes(), n => n is SessionNode s && s.Session.Id == entry.Id) is { } existing)
            {
                existing.IsSelected = true;
                BringIntoView(existing);
            }
        }

        if (result.Connect)
        {
            Connect(entry);
        }
    }

    private void EditSession(SessionEntry entry)
    {
        if (!EnsureWritable())
        {
            return;
        }

        if (SessionEditorWindow.Edit(this, entry, _sessions.Document.AllFolders(), _vault, isNew: false, _sessions.Document.Sessions) is { } result)
        {
            // Logins podem ter mudado enquanto o editor estava aberto (ex.: aviso "salvar login").
            if (_sessions.Document.Sessions.FirstOrDefault(s => s.Id == result.Entry.Id) is { } current)
            {
                result.Entry.Logins = [.. current.Logins];
                result.Entry.ChooseLoginOnConnect = current.ChooseLoginOnConnect;
            }

            _sessions.AddOrUpdate(result.Entry);
            RebuildTree();
            if (result.Connect)
            {
                Connect(result.Entry);
            }
        }
    }

    private void DuplicateSession(SessionEntry entry)
    {
        if (!EnsureWritable())
        {
            return;
        }

        var copy = entry.Clone();
        copy.Name = entry.Name + " (cópia)";
        try
        {
            _logins.CopyAll(entry, copy);
        }
        catch (Win32Exception ex)
        {
            Log.Error("Copiar logins da sessão duplicada", ex);
            copy.Logins.Clear();
        }
        _sessions.AddOrUpdate(copy);
        RebuildTree();
    }

    private async Task NewFolder(string parent)
    {
        if (!EnsureWritable())
        {
            return;
        }

        var input = new Wpf.Ui.Controls.TextBox { PlaceholderText = "Nome da pasta", MinWidth = 300 };
        var box = new MessageBox
        {
            Title = parent.Length == 0 ? "Nova pasta" : $"Nova subpasta em {parent}",
            Content = input,
            PrimaryButtonText = "Criar",
            CloseButtonText = "Cancelar",
        };
        input.Loaded += (_, _) => input.Focus();
        if (await box.ShowDialogAsync() == MessageBoxResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var name = input.Text.Trim().Replace('/', '-').Replace('\\', '-');
            _sessions.AddFolder(parent.Length == 0 ? name : $"{parent}/{name}");
            RebuildTree();
        }
    }

    private async Task DeleteFolder(string path)
    {
        if (!EnsureWritable())
        {
            return;
        }

        if (!_sessions.TryDeleteEmptyFolder(path))
        {
            await ShowMessage("Pasta com sessões", "Só é possível excluir pastas vazias. Mova ou exclua as sessões antes.");
            return;
        }

        RebuildTree();
    }

    private bool EnsureWritable()
    {
        if (!_sessions.IsReadOnly)
        {
            return true;
        }

        _ = ShowMessage("Somente leitura", $"O arquivo de sessões ({_sessions.FilePath}) está com problema. Corrija-o ou restaure o .bak antes de alterar sessões.");
        return false;
    }

    // ------------------------------------------------------------------ importação

    /// <summary>Botão "Importar": MobaXterm ou OpenSSH (~/.ssh/config).</summary>
    private void OnImportMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Do MobaXterm (MobaXterm.ini)...", () => _ = ImportMobaXtermAsync()));
        menu.Items.Add(MenuItem("Do OpenSSH (~/.ssh/config)...", () => _ = ImportSshConfigAsync()));
        menu.IsOpen = true;
    }

    private async Task ImportSshConfigAsync()
    {
        if (!EnsureWritable())
        {
            return;
        }

        var path = SshConfigImporter.DefaultPath;
        if (!File.Exists(path))
        {
            var dialog = new OpenFileDialog { Title = "Localize o arquivo de configuração do OpenSSH (config)", Filter = "config|config|Todos os arquivos|*.*" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            path = dialog.FileName;
        }

        try
        {
            var (added, result) = _sessions.ImportSshConfig(path);
            RebuildTree();
            var skipped = result.Sessions.Count - added;
            var text = result.Sessions.Count == 0
                ? $"Nenhum \"Host\" encontrado em {path}."
                : $"{added} sessões importadas de {path} para a pasta \"{SshConfigImporter.Folder}\".";
            if (skipped > 0)
            {
                text += $"\n{skipped} já existiam e foram ignoradas.";
            }

            if (result.Warnings.Count > 0)
            {
                text += "\n\nAvisos:\n• " + string.Join("\n• ", result.Warnings.Distinct().Take(10));
            }

            await ShowMessage("Importação concluída", text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Importar ~/.ssh/config", ex);
            await ShowMessage("Não foi possível importar", ex.Message);
        }
    }

    private async Task ImportMobaXtermAsync()
    {
        if (!EnsureWritable())
        {
            return;
        }

        var ini = MobaXtermImporter.FindDefaultIniPath();
        if (ini is null)
        {
            var dialog = new OpenFileDialog { Title = "Localize o MobaXterm.ini", Filter = "MobaXterm.ini|MobaXterm.ini|Arquivos INI|*.ini" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            ini = dialog.FileName;
        }

        await ImportFrom(ini);
    }

    private async Task ImportFrom(string ini)
    {
        try
        {
            var (added, result) = _sessions.ImportMobaXterm(ini);
            RebuildTree();
            var skipped = result.Sessions.Count - added;
            var text = $"{added} sessões importadas de {ini}.";
            if (skipped > 0)
            {
                text += $"\n{skipped} já existiam e foram ignoradas.";
            }

            if (result.Warnings.Count > 0)
            {
                text += "\n\nAvisos:\n• " + string.Join("\n• ", result.Warnings.Take(10));
            }

            await ShowMessage("Importação concluída", text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Importar MobaXterm", ex);
            await ShowMessage("Não foi possível importar", ex.Message);
        }
    }

    // ------------------------------------------------------------------ terminal local e conexão rápida

    private void OnLocalShellMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LocalShellButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var shell in LocalShells.Available())
        {
            var kind = shell.Kind;
            var label = shell.DisplayName + (kind == _settings.DefaultLocalShell ? "  (padrão)" : "");
            menu.Items.Add(MenuItem(label, () =>
            {
                _settings.DefaultLocalShell = kind;
                SaveSettings();
                OpenLocalShell(kind);
            }));
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ última sessão de trabalho

    /// <summary>Guarda as abas abertas (sessões salvas, terminais locais e áreas de trabalho remotas).</summary>
    private void RememberWorkspace()
    {
        var tabs = new List<WorkspaceTab>();
        var seen = new HashSet<Guid>();
        foreach (var session in _workspace.Sessions)
        {
            var entry = session.Entry;
            if (entry.Kind == SessionKind.LocalShell)
            {
                tabs.Add(new WorkspaceTab { LocalShell = entry.LocalShell });
            }
            else if (_sessions.Contains(entry.Id) && seen.Add(entry.Id))
            {
                tabs.Add(new WorkspaceTab { SessionId = entry.Id });
            }
        }

        foreach (var entry in _rdp.OpenEntries.Concat(_vnc.OpenEntries))
        {
            if (_sessions.Contains(entry.Id) && seen.Add(entry.Id))
            {
                tabs.Add(new WorkspaceTab { SessionId = entry.Id });
            }
        }

        // Fechou sem nenhuma aba: mantém a oferta anterior (não apaga por engano).
        if (tabs.Count > 0)
        {
            _settings.LastWorkspace = tabs;
        }
    }

    private List<(WorkspaceTab Tab, string Name)> RestorableTabs() =>
        _settings.LastWorkspace
            .Select(t => (Tab: t, Name: t.SessionId is { } id
                ? _sessions.Document.Sessions.FirstOrDefault(s => s.Id == id)?.Name
                : t.LocalShell is { } shell ? LocalShells.Resolve(shell)?.DisplayName : null))
            .Where(t => t.Name is not null)
            .Select(t => (t.Tab, t.Name!))
            .ToList();

    /// <summary>Tela inicial: botão para reabrir as abas da última vez.</summary>
    private void OfferRestore()
    {
        var tabs = RestorableTabs();
        if (tabs.Count > 0)
        {
            _workspace.Send(new { type = "restoreOffer", names = tabs.Select(t => t.Name).ToList() });
        }
    }

    private void RestoreWorkspace()
    {
        foreach (var (tab, _) in RestorableTabs())
        {
            if (tab.SessionId is { } id && _sessions.Document.Sessions.FirstOrDefault(s => s.Id == id) is { } entry)
            {
                Connect(entry);
            }
            else if (tab.LocalShell is { } shell)
            {
                OpenLocalShell(shell);
            }
        }
    }

    private void OpenLocalShell(LocalShellKind kind)
    {
        var shell = LocalShells.Resolve(kind) ?? LocalShells.Resolve(LocalShellKind.PowerShell);
        if (shell is null)
        {
            return;
        }

        _workspace.Open(new SessionEntry
        {
            Kind = SessionKind.LocalShell,
            LocalShell = shell.Kind,
            Name = shell.DisplayName,
        });
    }

    private void OnQuickConnectKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            QuickConnect();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            QuickConnectBox.Text = "";
            _workspace.FocusTerminal();
        }
    }

    private void OnQuickConnect(object sender, RoutedEventArgs e) => QuickConnect();

    private void QuickConnect()
    {
        var text = QuickConnectBox.Text?.Trim() ?? "";
        // "telnet://equipamento[:porta]" abre em Telnet, "rdp://servidor" na Área de Trabalho Remota; o resto é SSH.
        var kind = SessionKind.Ssh;
        Core.Ftp.FtpSecurity? ftpSecurity = null;
        if (text.StartsWith("telnet://", StringComparison.OrdinalIgnoreCase))
        {
            kind = SessionKind.Telnet;
            text = text["telnet://".Length..];
        }
        else if (text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase))
        {
            kind = SessionKind.Rdp;
            text = text["rdp://".Length..];
        }
        else if (text.StartsWith("vnc://", StringComparison.OrdinalIgnoreCase))
        {
            kind = SessionKind.Vnc;
            text = text["vnc://".Length..];
        }
        else if (text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
        {
            kind = SessionKind.Ftp;
            ftpSecurity = text.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ? Core.Ftp.FtpSecurity.Explicit : Core.Ftp.FtpSecurity.None;
            text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }
        else if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["ssh://".Length..];
        }

        var match = QuickConnectPattern.Match(text);
        if (!match.Success)
        {
            _ = ShowMessage("Conexão rápida", "Digite o IP (ou nome) do servidor; opcionalmente usuario@host e :porta.");
            return;
        }

        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : SessionEntry.DefaultPort(kind);
        if (port is < 1 or > 65535)
        {
            _ = ShowMessage("Conexão rápida", "Porta inválida (1 a 65535).");
            return;
        }

        var host = match.Groups["host"].Value;
        var user = match.Groups["user"].Success ? match.Groups["user"].Value : null;

        // Se a sessão já existe salva, usa a configuração dela (chave, ambiente, senha salva).
        var saved = _sessions.Document.Sessions.FirstOrDefault(s =>
            s.Kind == kind &&
            s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port &&
            (user is null || string.Equals(s.Username, user, StringComparison.Ordinal)));

        Connect(saved ?? new SessionEntry
        {
            Name = text,
            Kind = kind,
            Host = host,
            Port = port,
            Username = user,
            Ftp = ftpSecurity is { } security ? new Core.Ftp.FtpSettings { Security = security } : null,
        });
        QuickConnectBox.Text = "";
    }

    private void FocusQuickConnect()
    {
        QuickConnectBox.Focus();
        Keyboard.Focus(QuickConnectBox);
        QuickConnectBox.SelectAll();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.K)
            {
                e.Handled = true;
                FocusQuickConnect();
            }
            else if (e.Key == Key.T)
            {
                e.Handled = true;
                OpenLocalShell(_settings.DefaultLocalShell);
            }
            else if (e.Key == Key.L)
            {
                e.Handled = true;
                SetCommandsPanel(!IsCommandsPanelOpen);
            }
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    // ------------------------------------------------------------------ status

    private void UpdateSummary()
    {
        var saved = _sessions.Document.Sessions.Count;
        var open = _workspace.OpenCount;
        var connected = _workspace.ConnectedCount;
        SummaryText.Text = $"{Plural(saved, "sessão salva", "sessões salvas")}  ·  {Plural(open, "aberta", "abertas")}  ·  {Plural(connected, "conectada", "conectadas")}";
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private void UpdateActiveStatus(TerminalSession? session)
    {
        // Barra de status e painel de arquivos acompanham o terminal ativo.
        ServerBar.DataContext = session?.ServerStatus;
        ServerBar.Visibility = session?.ServerStatus is not null ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Visibility = session?.ServerStatus is null ? Visibility.Visible : Visibility.Collapsed;
        if (session is null || session.IsSsh)
        {
            FilesView.DataContext = session?.Files;
        }

        // Área de Trabalho Remota ativa: o painel de arquivos oferece os arquivos do servidor Windows.
        var remoteDesktop = session is null ? _rdp.Describe(_workspace.ActivePaneId) : null;
        FilesView.RemoteDesktop = remoteDesktop;

        // Título da janela (barra de tarefas, Alt+Tab): o que está ativo, como no VS Code.
        var context = session?.Entry.Name ?? remoteDesktop?.Name;
        Title = string.IsNullOrWhiteSpace(context) ? "RootDock" : $"{context} — RootDock";

        if (session is { IsSsh: true, State: TerminalState.Connected, Files: not null } && _autoSwitchedToFiles.Add(session.Id))
        {
            // Como no MobaXterm: ao conectar, mostra os arquivos do servidor.
            FilesTab.IsChecked = true;
        }

        if (FilesView.IsVisible)
        {
            FilesView.Activate();
        }

        UpdateCommandTarget(session);
        UpdateTunnelsBadge(session);

        if (session is null)
        {
            ActiveDot.Visibility = Visibility.Collapsed;
            ActiveText.Text = "";
            ProdBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var (brushKey, state) = session.State switch
        {
            TerminalState.Connected => ("RdDev", "conectado"),
            TerminalState.Closed => ("RdTextDim", "desconectado"),
            _ => ("RdStaging", "conectando"),
        };
        ActiveDot.Fill = (Brush)FindResource(brushKey);
        ActiveDot.Visibility = Visibility.Visible;
        ActiveText.Text = $"{session.Entry.Endpoint}  —  {state}";
        ProdBadge.Visibility = session.Entry.Environment == SessionEnvironment.Production ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSideTabChanged(object sender, RoutedEventArgs e)
    {
        if (FilesView is null || SessionsView is null)
        {
            return; // durante o InitializeComponent
        }

        var files = FilesTab.IsChecked == true;
        FilesView.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        SessionsView.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
        if (files)
        {
            FilesView.Activate();
        }
    }

    /// <summary>Aviso rápido na barra de status (some sozinho).</summary>
    private void Notify(string text, bool isError)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = (Brush)FindResource(isError ? "RdProd" : "RdText");
        NoticeBar.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ------------------------------------------------------------------ janela e preferências

    private void RestorePlacement()
    {
        SidebarColumn.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 180, 600));

        // Primeira abertura: nunca maior que a área útil (telas pequenas ou com escala de 125%/150%).
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.92);
        Height = Math.Min(Height, work.Height * 0.92);

        var placement = _settings.Window;
        if (placement is null || placement.Width < 400 || placement.Height < 300)
        {
            return;
        }

        // Só restaura se a posição ainda cabe na área visível (monitores podem ter mudado).
        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!visible.IntersectsWith(rect))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        var unsaved = _workspace.Editors?.DirtyCount ?? 0;
        if (!_closeConfirmed && (_workspace.ConnectedCount > 0 || unsaved > 0))
        {
            e.Cancel = true;
            var lines = new List<string>();
            if (unsaved > 0)
            {
                lines.Add(unsaved == 1
                    ? "1 arquivo aberto no editor tem alterações não salvas; elas serão perdidas."
                    : $"{unsaved} arquivos abertos no editor têm alterações não salvas; elas serão perdidas.");
            }

            if (_workspace.ConnectedCount > 0)
            {
                lines.Add(_workspace.ConnectedCount == 1 ? "Há 1 conexão ativa. Fechar encerra a conexão." : $"Há {_workspace.ConnectedCount} conexões ativas. Fechar encerra todas.");
            }

            var restarting = Updates?.RestartRequested == true;
            if (restarting)
            {
                lines.Add($"O RootDock vai reiniciar já na versão {Updates!.AvailableVersion}. Depois, as abas podem ser reabertas com um clique.");
            }

            var box = new MessageBox
            {
                Title = restarting ? "Reiniciar e atualizar" : "Fechar o RootDock",
                Content = string.Join("\n\n", lines),
                PrimaryButtonText = restarting ? "Reiniciar e atualizar" : unsaved > 0 ? "Fechar sem salvar" : "Fechar tudo",
                CloseButtonText = "Cancelar",
            };
            if (await box.ShowDialogAsync() == MessageBoxResult.Primary)
            {
                _closeConfirmed = true;
                // A confirmação roda dentro do evento Closing: fechar de novo só depois que ele termina.
                _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
            }
            else
            {
                Updates?.CancelRestart();
            }

            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        RememberWorkspace();
        RememberCommandsWidth();
        SaveSettings();
        _editor.Dispose();
        _rdp.CloseAll();
        _vnc.CloseAll();
        _workspace.CloseAll();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (IOException ex)
        {
            Log.Error("Salvar preferências", ex);
        }
    }

    private Task<MessageBoxResult> ShowMessage(string title, string text) =>
        new MessageBox { Title = title, Content = text, CloseButtonText = "OK" }.ShowDialogAsync();
}
