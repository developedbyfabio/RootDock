using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Renci.SshNet;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.Core.History;
using RootDock.Core.Sftp;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RootDock.App.Dialogs;

/// <summary>
/// Gerenciador de arquivos em duas colunas (este computador | servidor): arrastar entre os lados,
/// fila com pausar/retomar, comparar pastas, sincronizar sem apagar nada, compactar/descompactar
/// no servidor, dono do arquivo, tamanho de pasta e histórico de transferências.
/// </summary>
public partial class FileManagerWindow : FluentWindow
{
    private static readonly Brush OnlyHere = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7)));
    private static readonly Brush Different = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68)));
    private static readonly Brush Same = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x70)));
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA5)));
    private static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0x76, 0x8E)));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly FileManagerSource _source;
    private readonly TransferQueue _transfers;
    private readonly TransferHistory _history;
    private string _localPath;
    private string _remotePath;
    private Point _dragStart;
    private ListBox? _dragSource;

    private sealed class FileRow
    {
        public FileRow(RemoteEntry entry, bool local)
        {
            Entry = entry;
            IsLocal = local;
            var look = new RemoteEntryViewModel(entry);
            Icon = look.Icon;
            IconBrush = look.IconBrush;
            IconImage = look.IconImage;
            GlyphVisibility = look.GlyphVisibility;
            SizeText = entry.IsDirectory ? "" : Format.Bytes(entry.Size);
            ModifiedText = entry.Modified == default ? "" : entry.Modified.ToString("dd/MM/yy HH:mm"); // horário local dos dois lados
            Extra = local ? "" : entry.Permissions;
            ToolTip = entry.FullPath;
        }

        public RemoteEntry Entry { get; }

        public bool IsLocal { get; }

        public string Name => Entry.Name;

        public SymbolRegular Icon { get; }

        public Brush IconBrush { get; }

        public ImageSource? IconImage { get; }

        public Visibility GlyphVisibility { get; }

        public string SizeText { get; }

        public string ModifiedText { get; }

        public string Extra { get; set; }

        public Brush ExtraBrush { get; set; } = Dim;

        public FontFamily ExtraFont { get; set; } = Mono;

        public string ToolTip { get; set; }

        public double Opacity => Entry.IsHidden ? 0.65 : 1;

        public override string ToString() => Name; // leitores de tela e automação
    }

    private sealed record HistoryRow(TransferRecord Record)
    {
        public string When => Record.Time.LocalDateTime.ToString("dd/MM HH:mm:ss");

        public string Glyph => Record.Upload ? "↑" : "↓";

        public string Name => Record.Name;

        public string Server => Record.Server ?? "";

        public string Size => Record.Bytes > 0 ? Format.Bytes(Record.Bytes) : "";

        public string Result => Record.Result;

        public Brush ResultBrush => Record.Result.StartsWith("Concluído", StringComparison.Ordinal) ? Same : Record.Result.StartsWith("Cancel", StringComparison.Ordinal) ? Dim : Bad;
    }

    private FileManagerWindow(FileManagerSource source, TransferQueue transfers, TransferHistory history)
    {
        InitializeComponent();
        _source = source;
        _transfers = transfers;
        _history = history;
        TitleBar.Title = $"Arquivos · {source.Name}";
        Title = TitleBar.Title;
        RemoteTitle.Text = $"Servidor ({source.Protocol}) · {source.Name}" + (source.IsProduction ? "  (PRODUÇÃO)" : "");
        _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _remotePath = source.StartPath is { Length: > 0 } start ? start : "/";
        Closed += (_, _) =>
        {
            _transfers.Items.CollectionChanged -= OnQueueChanged;
            _history.Changed -= OnHistoryChanged;
            source.Closed?.Invoke();
        };
        QueueList.ItemsSource = _transfers.Items;
        _transfers.Items.CollectionChanged += OnQueueChanged;
        _history.Changed += OnHistoryChanged;
        UpdateQueueEmpty();
        BuildPlaces();
        Loaded += async (_, _) =>
        {
            LoadLocal();
            await LoadRemoteAsync();
        };
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateQueueEmpty();

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(RefreshHistory);

    /// <summary>Abre o gerenciador (SFTP de um terminal SSH, ou FTP/FTPS).</summary>
    public static void ShowFor(Window owner, FileManagerSource source, TransferQueue transfers, TransferHistory history) =>
        new FileManagerWindow(source, transfers, history) { Owner = owner }.Show();

    /// <summary>Gerenciador para os arquivos (SFTP) de um terminal SSH.</summary>
    public static FileManagerSource SftpSource(RemoteFilesViewModel vm, Func<SshClient?> ssh, EditorService? editor) => new()
    {
        Name = vm.Session.Name,
        IsProduction = vm.IsProduction,
        StartPath = vm.CurrentPath,
        Protocol = "SFTP",
        Connect = async () => await vm.GetFileSystemAsync(),
        Ssh = ssh,
        Edit = editor is null ? null : entry => editor.OpenAsync(vm, entry),
    };

    // ------------------------------------------------------------------ computador
    private void BuildPlaces()
    {
        var places = new List<(string Name, string Path)>
        {
            ("Pasta pessoal", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ("Área de trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
        };
        places = places.Where(p => Directory.Exists(p.Path)).ToList();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            places.Add(($"Unidade {drive.Name.TrimEnd('\\')}{(string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" ({drive.VolumeLabel})")}", drive.RootDirectory.FullName));
        }

        PlacesBox.Tag = places;
        PlacesBox.ItemsSource = places.Select(p => p.Name).ToList();
        PlacesBox.SelectedIndex = 0;
    }

    private void OnPlace(object sender, SelectionChangedEventArgs e)
    {
        if (PlacesBox.SelectedIndex >= 0 && PlacesBox.Tag is List<(string Name, string Path)> places)
        {
            _localPath = places[PlacesBox.SelectedIndex].Path;
            LoadLocal();
        }
    }

    private void LoadLocal()
    {
        try
        {
            var dir = new DirectoryInfo(_localPath);
            var rows = dir.EnumerateFileSystemInfos()
                .Where(i => !i.Attributes.HasFlag(FileAttributes.System) || i is DirectoryInfo)
                .Select(i => new FileRow(new RemoteEntry(i.Name, i.FullName, i is DirectoryInfo, false, i is FileInfo f ? f.Length : 0,
                    i.LastWriteTime, "", 0, 0), local: true))
                .OrderByDescending(r => r.Entry.IsDirectory)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            LocalList.ItemsSource = rows;
            LocalPathBox.Text = _localPath;
            LocalFooter.Text = Footer(rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalFooter.Text = "Não foi possível abrir: " + ex.Message;
        }

        CompareSummary.Text = "";
    }

    private static string Footer(IReadOnlyList<FileRow> rows)
    {
        var folders = rows.Count(r => r.Entry.IsDirectory);
        var files = rows.Count - folders;
        return $"{folders} pasta(s) · {files} arquivo(s) · {Format.Bytes(rows.Where(r => !r.Entry.IsDirectory).Sum(r => r.Entry.Size))}";
    }

    private void OnLocalUp(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_localPath) is { } parent)
        {
            _localPath = parent.FullName;
            LoadLocal();
        }
    }

    private void OnLocalRefresh(object sender, RoutedEventArgs e) => LoadLocal();

    private void OnLocalPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Directory.Exists(LocalPathBox.Text.Trim()))
        {
            _localPath = LocalPathBox.Text.Trim();
            LoadLocal();
        }
    }

    private void OnLocalOpen(object sender, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not FileRow row)
        {
            return;
        }

        if (row.Entry.IsDirectory)
        {
            _localPath = row.Entry.FullPath;
            LoadLocal();
        }
        else
        {
            Process.Start(new ProcessStartInfo(row.Entry.FullPath) { UseShellExecute = true });
        }
    }

    private void OnLocalKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Back:
                OnLocalUp(sender, e);
                break;
            case Key.Enter:
                OnLocalOpen(sender, null!);
                break;
            case Key.F2:
                OnLocalRename(sender, e);
                break;
            case Key.F5:
                LoadLocal();
                break;
            case Key.F7:
                OnLocalNewFolder(sender, e);
                break;
            case Key.Delete:
                OnLocalDelete(sender, e);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnLocalMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = LocalList.ContextMenu!;
        menu.Items.Clear();
        var selected = LocalList.SelectedItems.Cast<FileRow>().ToList();
        if (selected.Count > 0)
        {
            menu.Items.Add(Item($"Enviar para o servidor ({selected.Count})", () => _ = UploadAsync(selected.Select(r => r.Entry.FullPath).ToList())));
            if (selected.Count == 1)
            {
                menu.Items.Add(Item("Mostrar no Explorer", () => Process.Start("explorer.exe", $"/select,\"{selected[0].Entry.FullPath}\"")));
            }

            menu.Items.Add(Item("Copiar caminho", () => Clipboard.SetText(string.Join("\n", selected.Select(r => r.Entry.FullPath)))));
            menu.Items.Add(new Separator());
            if (selected.Count == 1)
            {
                menu.Items.Add(Item("Renomear (F2)", () => OnLocalRename(this, null!)));
            }

            menu.Items.Add(Item($"Mover para a Lixeira ({selected.Count})", () => OnLocalDelete(this, null!)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Nova pasta (F7)", () => OnLocalNewFolder(this, null!)));
        menu.Items.Add(Item("Abrir esta pasta no Explorer", () => Process.Start("explorer.exe", $"\"{_localPath}\"")));
    }

    // ------------------------------------------------------------------ servidor
    private async Task LoadRemoteAsync()
    {
        try
        {
            var fs = await _source.Connect();
            var entries = await fs.ListAsync(_remotePath, CancellationToken.None);
            var rows = entries
                .Select(e => new FileRow(e, local: false))
                .OrderByDescending(r => r.Entry.IsDirectory)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
            RemoteList.ItemsSource = rows;
            RemotePathBox.Text = ShowRemote(_remotePath);
            RemoteFooter.Text = Footer(rows);
        }
        catch (Exception ex)
        {
            RemoteFooter.Text = "Não foi possível abrir: " + ex.Message;
        }

        CompareSummary.Text = "";
    }

    private async Task NavigateRemoteAsync(string path)
    {
        _remotePath = path;
        await LoadRemoteAsync();
    }

    private void OnRemoteUp(object sender, RoutedEventArgs e) => _ = NavigateRemoteAsync(RemotePath.Parent(_remotePath));

    private async void OnRemoteHome(object sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateRemoteAsync((await _source.Connect()).HomeDirectory);
        }
        catch (Exception ex)
        {
            RemoteFooter.Text = ex.Message;
        }
    }

    private void OnRemoteRefresh(object sender, RoutedEventArgs e) => _ = LoadRemoteAsync();

    private void OnRemotePathKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RemotePathBox.Text.Trim().Length == 0)
        {
            return;
        }

        var typed = RemotePathBox.Text.Trim();
        if ((_source.ReadPath is { } read ? read(typed) : typed) is { } path)
        {
            _ = NavigateRemoteAsync(path);
        }
        else
        {
            RemoteFooter.Text = $"Caminho inválido para este servidor: {typed} (ex.: C:\\pasta ou {ShowRemote("/C$")}).";
        }
    }

    /// <summary>Caminho do servidor como a pessoa lê (servidor Windows: \\servidor\C$\pasta).</summary>
    private string ShowRemote(string path)
    {
        try
        {
            return _source.ShowPath?.Invoke(path) ?? path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private async void OnRemoteOpen(object sender, MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileRow row)
        {
            return;
        }

        if (row.Entry.IsDirectory)
        {
            await NavigateRemoteAsync(row.Entry.FullPath);
        }
        else
        {
            try
            {
                if (_source.Edit is { } edit)
                {
                    await edit(row.Entry);
                    Owner?.Activate();
                }
                else if (_source.WindowsPath is { } windowsPath)
                {
                    await OpenOnServerAsync(row.Entry, windowsPath(row.Entry));
                }
                else
                {
                    await OpenCopyAsync(row.Entry);
                }
            }
            catch (Exception ex)
            {
                await Prompts.InfoAsync("Não foi possível abrir", ex.Message);
            }
        }
    }

    private void OnRemoteKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Back:
                OnRemoteUp(sender, e);
                break;
            case Key.Enter:
                OnRemoteOpen(sender, null!);
                break;
            case Key.F2:
                OnRemoteRename(sender, e);
                break;
            case Key.F5:
                _ = LoadRemoteAsync();
                break;
            case Key.F7:
                OnRemoteNewFolder(sender, e);
                break;
            case Key.Delete:
                OnRemoteDelete(sender, e);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnRemoteMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = RemoteList.ContextMenu!;
        menu.Items.Clear();
        var selected = RemoteList.SelectedItems.Cast<FileRow>().ToList();
        var one = selected.Count == 1 ? selected[0] : null;
        if (selected.Count > 0)
        {
            menu.Items.Add(Item($"Baixar para o computador ({selected.Count})", () => _ = DownloadAsync(selected)));
        }

        if (one is { Entry.IsDirectory: false })
        {
            var open = _source.Edit is not null ? "Editar no RootDock"
                : _source.WindowsPath is not null ? "Abrir (salvar grava direto no servidor)"
                : "Abrir uma cópia (somente leitura)";
            menu.Items.Add(Item(open, () => OnRemoteOpen(this, null!)));
        }

        if (one is not null && _source.WindowsPath is { } windowsPath)
        {
            menu.Items.Add(Item("Mostrar no Explorer do Windows", () => ShowInExplorer(windowsPath(one.Entry), one.Entry.IsDirectory)));
        }

        if (one is not null)
        {
            menu.Items.Add(new Separator());
            if (_source.Ssh is not null)
            {
                // Comandos no servidor: só com SSH (no FTP não há como executá-los).
                if (one.Entry.IsDirectory)
                {
                    menu.Items.Add(Item("Tamanho da pasta (du)", () => _ = DirectorySizeAsync(one)));
                }

                if (ArchiveCommand(one.Entry, "x") is not null)
                {
                    menu.Items.Add(Item("Descompactar aqui (numa pasta nova)", () => _ = ExtractAsync(one)));
                }

                menu.Items.Add(Item("Compactar (.tar.gz)", () => _ = CompressAsync(one)));
                menu.Items.Add(Item("Trocar dono (chown)...", () => _ = ChangeOwnerAsync(one)));
            }

            menu.Items.Add(Item("Copiar caminho", () => Clipboard.SetText(_source.WindowsPath?.Invoke(one.Entry) ?? one.Entry.FullPath)));
            menu.Items.Add(Item("Renomear (F2)", () => OnRemoteRename(this, null!)));
        }

        if (selected.Count > 0)
        {
            menu.Items.Add(Item($"Excluir do servidor ({selected.Count})", () => OnRemoteDelete(this, null!)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Nova pasta (F7)", () => OnRemoteNewFolder(this, null!)));
        menu.Items.Add(Item("Atualizar (F5)", () => _ = LoadRemoteAsync()));
    }

    // ------------------------------------------------------------------ organizar (nova pasta, renomear, excluir)
    private async void OnLocalNewFolder(object sender, RoutedEventArgs e)
    {
        if (await Prompts.AskTextAsync("Nova pasta no computador", "Nome da pasta", primary: "Criar") is not { } name ||
            await InvalidName(name.Trim(), windows: true))
        {
            return;
        }

        var path = Path.Combine(_localPath, name.Trim());
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                await Prompts.InfoAsync("Já existe", $"Já existe \"{name.Trim()}\" em {_localPath}.");
                return;
            }

            Directory.CreateDirectory(path);
            LoadLocal();
            SelectByName(LocalList, name.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Prompts.InfoAsync("Não foi possível criar a pasta", ex.Message);
        }
    }

    private async void OnLocalRename(object sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItems.Count != 1 || LocalList.SelectedItem is not FileRow row)
        {
            LocalFooter.Text = "Selecione um item para renomear.";
            return;
        }

        if (await Prompts.AskTextAsync("Renomear", "Novo nome", row.Name, "Renomear") is not { } name ||
            name.Trim() == row.Name || await InvalidName(name.Trim(), windows: true))
        {
            return;
        }

        var target = Path.Combine(_localPath, name.Trim());
        try
        {
            if ((Directory.Exists(target) || File.Exists(target)) && !string.Equals(target, row.Entry.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                await Prompts.InfoAsync("Já existe", $"Já existe \"{name.Trim()}\" nesta pasta.");
                return;
            }

            if (row.Entry.IsDirectory)
            {
                Directory.Move(row.Entry.FullPath, target);
            }
            else
            {
                File.Move(row.Entry.FullPath, target);
            }

            LoadLocal();
            SelectByName(LocalList, name.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Prompts.InfoAsync("Não foi possível renomear", ex.Message);
        }
    }

    /// <summary>No computador vai para a Lixeira (dá para recuperar).</summary>
    private async void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        var rows = LocalList.SelectedItems.Cast<FileRow>().ToList();
        if (rows.Count == 0)
        {
            LocalFooter.Text = "Selecione o que excluir.";
            return;
        }

        var names = string.Join("\n", rows.Take(10).Select(r => r.Entry.IsDirectory ? r.Name + "\\" : r.Name)) + (rows.Count > 10 ? $"\n... e mais {rows.Count - 10}" : "");
        if (!await Prompts.ConfirmAsync("Mover para a Lixeira?", $"{names}\n\nDá para recuperar pela Lixeira do Windows.", "Mover para a Lixeira", danger: true))
        {
            return;
        }

        foreach (var row in rows)
        {
            try
            {
                if (row.Entry.IsDirectory)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(row.Entry.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.Entry.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            catch (OperationCanceledException)
            {
                break; // cancelado na janela de erro do Windows
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Prompts.InfoAsync("Não foi possível excluir", $"{row.Name}: {ex.Message}");
            }
        }

        LoadLocal();
    }

    private async void OnRemoteNewFolder(object sender, RoutedEventArgs e)
    {
        if (await Prompts.AskTextAsync("Nova pasta no servidor", "Nome da pasta", primary: "Criar") is not { } name ||
            await InvalidName(name.Trim(), windows: _source.WindowsPath is not null))
        {
            return;
        }

        var path = RemotePath.Combine(_remotePath, name.Trim());
        try
        {
            var fs = await _source.Connect();
            if (await fs.ExistsAsync(path, CancellationToken.None))
            {
                await Prompts.InfoAsync("Já existe", $"Já existe \"{name.Trim()}\" em {ShowRemote(_remotePath)}.");
                return;
            }

            await fs.CreateDirectoryAsync(path, CancellationToken.None);
            await LoadRemoteAsync();
            SelectByName(RemoteList, name.Trim());
        }
        catch (Exception ex)
        {
            await Prompts.InfoAsync("Não foi possível criar a pasta", ex.Message);
        }
    }

    private async void OnRemoteRename(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItems.Count != 1 || RemoteList.SelectedItem is not FileRow row)
        {
            RemoteFooter.Text = "Selecione um item para renomear.";
            return;
        }

        if (await Prompts.AskTextAsync("Renomear no servidor", "Novo nome", row.Name, "Renomear") is not { } name ||
            name.Trim() == row.Name || await InvalidName(name.Trim(), windows: _source.WindowsPath is not null))
        {
            return;
        }

        var target = RemotePath.Combine(RemotePath.Parent(row.Entry.FullPath), name.Trim());
        try
        {
            var fs = await _source.Connect();
            if (await fs.ExistsAsync(target, CancellationToken.None) && !string.Equals(target, row.Entry.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                await Prompts.InfoAsync("Já existe", $"Já existe \"{name.Trim()}\" nesta pasta do servidor.");
                return;
            }

            await fs.RenameAsync(row.Entry.FullPath, target, CancellationToken.None);
            await LoadRemoteAsync();
            SelectByName(RemoteList, name.Trim());
        }
        catch (Exception ex)
        {
            await Prompts.InfoAsync("Não foi possível renomear", ex.Message);
        }
    }

    /// <summary>No servidor não há lixeira: confirma mostrando o que será apagado.</summary>
    private async void OnRemoteDelete(object sender, RoutedEventArgs e)
    {
        var rows = RemoteList.SelectedItems.Cast<FileRow>().ToList();
        if (rows.Count == 0)
        {
            RemoteFooter.Text = "Selecione o que excluir.";
            return;
        }

        var names = string.Join("\n", rows.Take(10).Select(r => r.Entry.IsDirectory ? r.Name + "/  (com tudo o que tem dentro)" : r.Name)) +
                    (rows.Count > 10 ? $"\n... e mais {rows.Count - 10}" : "");
        var text = $"Em {ShowRemote(_remotePath)} ({_source.Name}):\n\n{names}\n\nNão há lixeira no servidor: não dá para desfazer." +
                   (_source.IsProduction ? "\n\nATENÇÃO: este é um servidor de PRODUÇÃO." : "");
        if (!await Prompts.ConfirmAsync("Excluir do servidor permanentemente?", text, "Excluir", danger: true))
        {
            return;
        }

        try
        {
            var fs = await _source.Connect();
            foreach (var row in rows)
            {
                RemoteFooter.Text = $"Excluindo {row.Name}...";
                await fs.DeleteAsync(row.Entry, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            await Prompts.InfoAsync("Não foi possível excluir tudo", ex.Message);
        }

        await LoadRemoteAsync();
    }

    /// <summary>Nome vazio, "." ou "..", com barra, ou (Windows) com caracteres proibidos: avisa e devolve true.</summary>
    private static async Task<bool> InvalidName(string name, bool windows)
    {
        string? problem = name.Length == 0 || name is "." or ".." ? "Digite um nome."
            : name.IndexOfAny(['/', '\\']) >= 0 ? "O nome não pode conter / nem \\."
            : windows && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "O nome tem caracteres que o Windows não aceita (: * ? \" < > |)."
            : windows && name.EndsWith('.') ? "No Windows o nome não pode terminar com ponto."
            : null;
        if (problem is null)
        {
            return false;
        }

        await Prompts.InfoAsync("Nome inválido", problem);
        return true;
    }

    private static void SelectByName(ListBox list, string name)
    {
        if (list.ItemsSource is IEnumerable<FileRow> rows && rows.FirstOrDefault(r => r.Name == name) is { } row)
        {
            list.SelectedItem = row;
            list.ScrollIntoView(row);
        }
    }

    /// <summary>Programas e scripts do servidor nunca são abertos por aqui: rodariam neste computador.</summary>
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".scr", ".pif", ".cpl", ".lnk", ".reg", ".jar", ".application", ".appref-ms", ".url", ".chm",
    };

    private static bool IsExecutable(string name) => Executable.Contains(Path.GetExtension(name));

    /// <summary>Servidor Windows: abre o arquivo do próprio servidor (salvar grava lá).</summary>
    private async Task OpenOnServerAsync(RemoteEntry entry, string path)
    {
        if (IsExecutable(entry.Name))
        {
            await Prompts.InfoAsync("Arquivo executável", $"\"{entry.Name}\" é um programa ou script: abrir aqui o executaria neste computador, não no servidor. Use \"Mostrar no Explorer do Windows\" se precisar.");
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        RemoteFooter.Text = $"Aberto direto do servidor: ao salvar, a alteração já fica em {_source.Name}.";
    }

    private static void ShowInExplorer(string path, bool isDirectory) =>
        Process.Start("explorer.exe", isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"");

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ------------------------------------------------------------------ transferências
    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var paths = LocalList.SelectedItems.Cast<FileRow>().Select(r => r.Entry.FullPath).ToList();
        if (paths.Count == 0)
        {
            LocalFooter.Text = "Selecione o que enviar (Ctrl ou Shift para vários).";
            return;
        }

        _ = UploadAsync(paths);
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var rows = RemoteList.SelectedItems.Cast<FileRow>().ToList();
        if (rows.Count == 0)
        {
            RemoteFooter.Text = "Selecione o que baixar (Ctrl ou Shift para vários).";
            return;
        }

        _ = DownloadAsync(rows);
    }

    /// <summary>Envia, comparando antes o que já existe no servidor (substituir ou pular).</summary>
    private async Task UploadAsync(IReadOnlyList<string> paths)
    {
        var target = _remotePath;
        var fs = await _source.Connect();
        var conflicts = new List<string>();
        var conflictPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var remote = RemotePath.Combine(target, Path.GetFileName(path.TrimEnd('\\')));
            if (await fs.ExistsAsync(remote, CancellationToken.None))
            {
                conflictPaths.Add(path);
                var r = await fs.GetAsync(remote, CancellationToken.None);
                var localInfo = new FileInfo(path);
                conflicts.Add(Directory.Exists(path)
                    ? $"{r.Name}/  (pasta: os arquivos de dentro serão substituídos)"
                    : $"{r.Name}   computador: {Format.Bytes(localInfo.Length)}, {localInfo.LastWriteTime:dd/MM HH:mm}   servidor: {Format.Bytes(r.Size)}, {r.Modified.ToLocalTime():dd/MM HH:mm}");
            }
        }

        var send = paths.ToList();
        if (conflicts.Count > 0)
        {
            var box = new MessageBox
            {
                Title = $"{conflicts.Count} item(ns) já existe(m) no servidor",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"Em {ShowRemote(target)}:\n\n{string.Join("\n", conflicts.Take(10))}{(conflicts.Count > 10 ? $"\n... e mais {conflicts.Count - 10}" : "")}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                    FontFamily = Mono,
                    FontSize = 12,
                },
                PrimaryButtonText = "Substituir",
                SecondaryButtonText = "Pular os que existem",
                CloseButtonText = "Cancelar",
                PrimaryButtonAppearance = ControlAppearance.Danger,
            };
            var answer = await box.ShowDialogAsync();
            if (answer == MessageBoxResult.None)
            {
                return;
            }

            if (answer == MessageBoxResult.Secondary)
            {
                send = paths.Where(p => !conflictPaths.Contains(p)).ToList();
            }
        }

        if (send.Count > 0)
        {
            TransferJobs.Upload(_transfers, _source, send, target);
            QueueTab.IsChecked = true;
            _ = RefreshRemoteWhenDoneAsync();
        }
    }

    /// <summary>Baixa para a pasta do computador (o que já existe ganha "nome (1)").</summary>
    private async Task DownloadAsync(IReadOnlyList<FileRow> rows)
    {
        var existing = rows.Where(r => File.Exists(Path.Combine(_localPath, r.Name)) || Directory.Exists(Path.Combine(_localPath, r.Name))).Select(r => r.Name).ToList();
        if (existing.Count > 0 && !await Prompts.ConfirmAsync(
                "Já existe no computador",
                $"{string.Join(", ", existing.Take(6))}{(existing.Count > 6 ? "..." : "")} já existe(m) em {_localPath}.\n\nOs arquivos novos serão salvos como \"nome (1)\", sem apagar os atuais.",
                "Baixar mesmo assim"))
        {
            return;
        }

        TransferJobs.Download(_transfers, _source, rows.Select(r => r.Entry).ToList(), _localPath);
        QueueTab.IsChecked = true;
        _ = RefreshLocalWhenDoneAsync();
    }

    private async Task RefreshRemoteWhenDoneAsync()
    {
        await WaitForQueueAsync();
        await LoadRemoteAsync();
    }

    private async Task RefreshLocalWhenDoneAsync()
    {
        await WaitForQueueAsync();
        LoadLocal();
    }

    private async Task WaitForQueueAsync()
    {
        while (_transfers.Items.Any(i => i.State is TransferState.Waiting or TransferState.Running))
        {
            await Task.Delay(700);
        }
    }

    private void UpdateQueueEmpty()
    {
        QueueEmpty.Visibility = QueueTab.IsChecked == true && _transfers.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseAllButton.Content = _transfers.IsPaused ? "Retomar tudo" : "Pausar tudo";
    }

    private void OnPauseAll(object sender, RoutedEventArgs e)
    {
        if (_transfers.IsPaused)
        {
            _transfers.ResumeAllCommand.Execute(null);
        }
        else
        {
            _transfers.PauseAllCommand.Execute(null);
        }

        UpdateQueueEmpty();
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => _transfers.ClearFinishedCommand.Execute(null);

    private void OnBottomTab(object sender, RoutedEventArgs e)
    {
        if (QueueList is null)
        {
            return;
        }

        var history = HistoryTab.IsChecked == true;
        QueueList.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (history)
        {
            RefreshHistory();
        }

        UpdateQueueEmpty();
    }

    private void RefreshHistory()
    {
        if (HistoryTab.IsChecked == true)
        {
            HistoryList.ItemsSource = _history.Recent().Select(r => new HistoryRow(r)).ToList();
        }
    }

    // ------------------------------------------------------------------ arrastar e soltar
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragSource = sender as ListBox;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null || sender != _dragSource)
        {
            return;
        }

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var rows = _dragSource.SelectedItems.Cast<FileRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetData("RootDockRows", rows);
        if (rows[0].IsLocal)
        {
            data.SetFileDropList([.. rows.Select(r => r.Entry.FullPath)]);
        }

        _dragSource = null;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var fromSelf = e.Data.GetData("RootDockRows") is List<FileRow> rows && rows.Count > 0 && rows[0].IsLocal == (sender == LocalList);
        e.Effects = fromSelf ? DragDropEffects.None
            : e.Data.GetDataPresent("RootDockRows") || (sender == RemoteList && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRemoteDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _ = UploadAsync(files);
        }
    }

    private void OnLocalDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("RootDockRows") is List<FileRow> rows && rows.Count > 0 && !rows[0].IsLocal)
        {
            _ = DownloadAsync(rows);
        }
    }

    // ------------------------------------------------------------------ comparar e sincronizar
    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        await LoadRemoteAsync();
        LoadLocal();
        var local = (LocalList.ItemsSource as IEnumerable<FileRow>)?.ToList() ?? [];
        var remote = (RemoteList.ItemsSource as IEnumerable<FileRow>)?.ToList() ?? [];
        var remoteByName = remote.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var localByName = local.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        int onlyLocal = 0, onlyRemote = 0, different = 0, same = 0;
        foreach (var row in local)
        {
            if (!remoteByName.TryGetValue(row.Name, out var other))
            {
                Mark(row, "só aqui", OnlyHere);
                onlyLocal++;
            }
            else if (row.Entry.IsDirectory || other.Entry.IsDirectory)
            {
                Mark(row, row.Entry.IsDirectory == other.Entry.IsDirectory ? "pasta nos dois" : "tipo diferente", Same);
            }
            else if (row.Entry.Size != other.Entry.Size)
            {
                Mark(row, "tamanho difere", Different);
                different++;
            }
            else
            {
                Mark(row, "igual", Same);
                same++;
            }
        }

        foreach (var row in remote)
        {
            if (!localByName.TryGetValue(row.Name, out var other))
            {
                Mark(row, "só no servidor", OnlyHere);
                onlyRemote++;
            }
            else if (!row.Entry.IsDirectory && !other.Entry.IsDirectory)
            {
                var newer = row.Entry.Modified > other.Entry.Modified.AddSeconds(2) ? "mais novo aqui" : other.Entry.Modified > row.Entry.Modified.AddSeconds(2) ? "mais antigo aqui" : "";
                Mark(row, row.Entry.Size != other.Entry.Size ? $"difere {newer}".Trim() : "igual", row.Entry.Size != other.Entry.Size ? Different : Same);
            }
            else
            {
                Mark(row, "pasta nos dois", Same);
            }
        }

        LocalList.Items.Refresh();
        RemoteList.Items.Refresh();
        CompareSummary.Text = $"{onlyLocal} só no computador · {onlyRemote} só no servidor · {different} com tamanho diferente · {same} iguais (comparação desta pasta, sem subpastas)";
    }

    private static void Mark(FileRow row, string text, Brush brush)
    {
        row.Extra = text;
        row.ExtraBrush = brush;
        row.ExtraFont = new FontFamily("Segoe UI");
    }

    private void OnSyncUp(object sender, RoutedEventArgs e) => _ = SyncAsync(upload: true);

    private void OnSyncDown(object sender, RoutedEventArgs e) => _ = SyncAsync(upload: false);

    /// <summary>
    /// Sincroniza a pasta atual (com subpastas) numa direção: envia/baixa o que falta ou tem
    /// tamanho diferente ou é mais novo na origem. Nunca apaga nada no destino.
    /// </summary>
    private async Task SyncAsync(bool upload)
    {
        var localRoot = _localPath;
        var remoteRoot = _remotePath;
        CompareSummary.Text = "Calculando o que precisa ser copiado...";
        List<(string Local, string Remote, long Size)> plan;
        var warnings = new List<string>();
        try
        {
            var fs = await _source.Connect();
            var remoteFiles = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
            var skippedLinks = new List<string>();
            await CollectRemoteAsync(fs, remoteRoot, "", remoteFiles, skippedLinks, depth: 0);
            if (skippedLinks.Count > 0)
            {
                warnings.Add($"{skippedLinks.Count} link(s) simbólico(s) ignorado(s) (ex.: {skippedLinks[0]}).");
            }
            var localFiles = Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .ToDictionary(f => Path.GetRelativePath(localRoot, f.FullName).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            plan = [];
            if (upload)
            {
                foreach (var (rel, info) in localFiles)
                {
                    // Horários locais nos dois lados (o SFTP entrega no fuso deste computador).
                    if (!remoteFiles.TryGetValue(rel, out var r) || r.Size != info.Length || info.LastWriteTime > r.Modified.AddSeconds(2))
                    {
                        plan.Add((info.FullName, RemotePath.Combine(remoteRoot, rel), info.Length));
                    }
                }
            }
            else
            {
                var refused = 0;
                foreach (var (rel, r) in remoteFiles)
                {
                    if (!localFiles.TryGetValue(rel, out var info) || r.Size != info.Length || r.Modified > info.LastWriteTime.AddSeconds(2))
                    {
                        // Nomes vindos do servidor são limpos parte a parte e o destino tem de ficar
                        // dentro da pasta escolhida (um nome com "..\" ou "C:" não sai dela).
                        if (LocalFiles.SafeCombine(localRoot, rel) is { } local)
                        {
                            plan.Add((local, r.FullPath, r.Size));
                        }
                        else
                        {
                            refused++;
                        }
                    }
                }

                if (refused > 0)
                {
                    warnings.Add($"{refused} arquivo(s) com nome inválido para o Windows foram ignorados.");
                }
            }
        }
        catch (Exception ex)
        {
            CompareSummary.Text = "";
            await Prompts.InfoAsync("Não foi possível comparar", ex.Message);
            return;
        }

        CompareSummary.Text = warnings.Count > 0 ? string.Join(" ", warnings) : "";
        if (plan.Count == 0)
        {
            await Prompts.InfoAsync("Tudo sincronizado", upload
                ? $"Nada para enviar: {remoteRoot} já tem tudo o que há em {localRoot}."
                : $"Nada para baixar: {localRoot} já tem tudo o que há em {remoteRoot}.");
            return;
        }

        var total = plan.Sum(p => p.Size);
        var list = string.Join("\n", plan.Take(12).Select(p => (upload ? p.Remote : p.Local) + $"  ({Format.Bytes(p.Size)})"));
        var production = upload && _source.IsProduction ? "\n\nATENÇÃO: o servidor é de PRODUÇÃO." : "";
        if (!await Prompts.ConfirmAsync(
                upload ? $"Enviar {plan.Count} arquivo(s) para o servidor?" : $"Baixar {plan.Count} arquivo(s) do servidor?",
                $"{Format.Bytes(total)} no total. Arquivos com o mesmo nome no destino serão atualizados; nada será apagado.{production}\n\n{list}{(plan.Count > 12 ? $"\n... e mais {plan.Count - 12}" : "")}",
                upload ? "Enviar" : "Baixar",
                danger: upload && _source.IsProduction))
        {
            return;
        }

        _transfers.Enqueue($"Sincronizar {(upload ? "→" : "←")} {Path.GetFileName(localRoot.TrimEnd('\\'))} ({plan.Count} arquivos)", upload, total, async item =>
        {
            var fs = await _source.Connect();
            long baseline = 0;
            foreach (var (local, remote, size) in plan)
            {
                item.Token.ThrowIfCancellationRequested();
                var start = baseline;
                if (upload)
                {
                    await EnsureRemoteDirectoryAsync(fs, RemotePath.Parent(remote), item.Token);
                    await fs.UploadFileAsync(local, remote, new Progress(b => item.SetDone(start + b)), item.Token);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    item.TrackPartial(local);
                    // Sincronizar atualiza de propósito (a lista foi confirmada antes).
                    await fs.DownloadFileAsync(remote, local, new Progress(b => item.SetDone(start + b)), item.Token,
                        () => item.PauseRequested, resume: item.Attempt > 1, overwrite: true);
                }

                baseline += size;
                item.SetDone(baseline);
            }
        }, _source.Name);
        QueueTab.IsChecked = true;
        _ = upload ? RefreshRemoteWhenDoneAsync() : RefreshLocalWhenDoneAsync();
    }

    /// <summary>
    /// Arquivos do servidor (com subpastas). Links simbólicos ficam de fora: um link para pasta
    /// (ex.: current → releases/123) pareceria arquivo e o download falharia.
    /// </summary>
    private static async Task CollectRemoteAsync(IFileTransport fs, string root, string relative, Dictionary<string, RemoteEntry> files, List<string> skippedLinks, int depth)
    {
        if (depth > 32 || files.Count > 50_000)
        {
            return;
        }

        var path = relative.Length == 0 ? root : RemotePath.Combine(root, relative);
        foreach (var entry in await fs.ListAsync(path, CancellationToken.None))
        {
            var rel = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.IsSymlink)
            {
                skippedLinks.Add(rel);
            }
            else if (entry.IsDirectory)
            {
                await CollectRemoteAsync(fs, root, rel, files, skippedLinks, depth + 1);
            }
            else
            {
                files[rel] = entry;
            }
        }
    }

    private static async Task EnsureRemoteDirectoryAsync(IFileTransport fs, string path, CancellationToken ct)
    {
        if (path is "/" or "" || await fs.ExistsAsync(path, ct))
        {
            return;
        }

        await EnsureRemoteDirectoryAsync(fs, RemotePath.Parent(path), ct);
        await fs.CreateDirectoryAsync(path, ct);
    }

    private sealed class Progress(Action<long> handler) : IProgress<long>
    {
        public void Report(long value) => handler(value);
    }

    // ------------------------------------------------------------------ comandos no servidor
    /// <summary>Roda um comando pelo SSH do terminal (sem sudo nem perguntas).</summary>
    private async Task<(int? Exit, string Output, string Error)?> RunAsync(string command)
    {
        if (_source.Ssh?.Invoke() is not { IsConnected: true } client)
        {
            await Prompts.InfoAsync("Terminal desconectado", "Esta ação usa a conexão SSH do terminal. Reconecte o terminal e tente de novo.");
            return null;
        }

        RemoteFooter.Text = "Executando no servidor...";
        try
        {
            using var cmd = client.CreateCommand(command);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await cmd.ExecuteAsync(limit.Token);
            return (cmd.ExitStatus, cmd.Result ?? "", cmd.Error ?? "");
        }
        catch (OperationCanceledException)
        {
            return (null, "", "Passou de 10 minutos: interrompido.");
        }
        finally
        {
            RemoteFooter.Text = "";
        }
    }

    private async Task DirectorySizeAsync(FileRow row)
    {
        if (await RunAsync($"du -sh -- {RemotePath.ShellQuote(row.Entry.FullPath)} 2>/dev/null; du -s --inodes -- {RemotePath.ShellQuote(row.Entry.FullPath)} 2>/dev/null | cut -f1") is not { } r)
        {
            return;
        }

        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var size = lines.Length > 0 ? lines[0].Split('\t')[0] : "?";
        var count = lines.Length > 1 ? $"\n{lines[1].Trim()} arquivos e pastas" : "";
        await Prompts.InfoAsync($"Tamanho de {row.Name}", $"{size}{count}\n\n{row.Entry.FullPath}" + (r.Error.Length > 0 ? $"\n\nAvisos: {r.Error.Trim()}" : ""));
    }

    private async Task CompressAsync(FileRow row)
    {
        var fs = await _source.Connect();
        var parent = RemotePath.Parent(row.Entry.FullPath);
        var name = row.Name + ".tar.gz";
        if (await fs.ExistsAsync(RemotePath.Combine(parent, name), CancellationToken.None))
        {
            name = $"{row.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz"; // nunca sobrescreve um arquivo existente
        }

        var command = $"cd {RemotePath.ShellQuote(parent)} && tar -czf {RemotePath.ShellQuote(name)} -- {RemotePath.ShellQuote(row.Name)}";
        if (await RunAsync(command) is not { } r)
        {
            return;
        }

        await LoadRemoteAsync();
        if (r.Exit != 0)
        {
            await Prompts.InfoAsync("Não foi possível compactar", r.Error.Trim().Length > 0 ? r.Error.Trim() : $"Código {r.Exit}.");
        }
        else
        {
            RemoteFooter.Text = $"Criado {name}";
        }
    }

    /// <summary>Comando de extração pelo tipo; extrai sempre numa pasta nova (nada é sobrescrito).</summary>
    private static string? ArchiveCommand(RemoteEntry entry, string destination)
    {
        var n = entry.Name.ToLowerInvariant();
        var a = RemotePath.ShellQuote(entry.FullPath);
        var d = RemotePath.ShellQuote(destination);
        return n.EndsWith(".tar.gz") || n.EndsWith(".tgz") ? $"tar -xzf {a} -C {d}"
            : n.EndsWith(".tar.bz2") || n.EndsWith(".tbz2") ? $"tar -xjf {a} -C {d}"
            : n.EndsWith(".tar.xz") || n.EndsWith(".txz") ? $"tar -xJf {a} -C {d}"
            : n.EndsWith(".tar") ? $"tar -xf {a} -C {d}"
            : n.EndsWith(".zip") ? $"unzip -n -q {a} -d {d}"
            : n.EndsWith(".gz") ? $"gunzip -c {a} > {RemotePath.ShellQuote(RemotePath.Combine(destination, entry.Name[..^3]))}"
            : null;
    }

    private async Task ExtractAsync(FileRow row)
    {
        var fs = await _source.Connect();
        var parent = RemotePath.Parent(row.Entry.FullPath);
        var baseName = row.Name;
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz", ".tar", ".zip", ".gz" })
        {
            if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^ext.Length];
                break;
            }
        }

        var destination = RemotePath.Combine(parent, baseName);
        for (var i = 2; await fs.ExistsAsync(destination, CancellationToken.None); i++)
        {
            destination = RemotePath.Combine(parent, $"{baseName} ({i})");
        }

        var command = $"mkdir -p {RemotePath.ShellQuote(destination)} && {ArchiveCommand(row.Entry, destination)}";
        if (await RunAsync(command) is not { } r)
        {
            return;
        }

        await LoadRemoteAsync();
        if (r.Exit != 0)
        {
            var hint = r.Error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "\n\nO programa de descompactação não está instalado no servidor (ex.: apt install unzip)." : "";
            await Prompts.InfoAsync("Não foi possível descompactar", (r.Error.Trim().Length > 0 ? r.Error.Trim() : $"Código {r.Exit}.") + hint);
        }
        else
        {
            RemoteFooter.Text = $"Extraído em {RemotePath.Name(destination)}/";
        }
    }

    private async Task ChangeOwnerAsync(FileRow row)
    {
        var current = await RunAsync($"stat -c '%U:%G' -- {RemotePath.ShellQuote(row.Entry.FullPath)}");
        if (current is null)
        {
            return;
        }

        var owner = await Prompts.AskTextAsync($"Dono de {row.Name}", "usuario:grupo (ex.: www-data:www-data)", current.Value.Output.Trim(), "Aplicar");
        if (string.IsNullOrWhiteSpace(owner) || owner.Trim() == current.Value.Output.Trim())
        {
            return;
        }

        owner = owner.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(owner, @"^[A-Za-z0-9._-]+(:[A-Za-z0-9._-]+)?$"))
        {
            await Prompts.InfoAsync("Dono inválido", "Use usuario ou usuario:grupo (letras, números, ponto, hífen e sublinhado).");
            return;
        }

        var recursive = row.Entry.IsDirectory && await Prompts.ConfirmAsync("Incluir o conteúdo?", $"Aplicar {owner} também a tudo dentro de {row.Name}/?", "Sim, recursivo");
        if (await RunAsync($"chown {(recursive ? "-R " : "")}{owner} -- {RemotePath.ShellQuote(row.Entry.FullPath)}") is not { } r)
        {
            return;
        }

        if (r.Exit != 0)
        {
            var hint = r.Error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) || r.Error.Contains("não permitida", StringComparison.OrdinalIgnoreCase)
                ? $"\n\nSó o root troca o dono. No terminal: sudo chown {(recursive ? "-R " : "")}{owner} {RemotePath.ShellQuote(row.Entry.FullPath)}"
                : "";
            await Prompts.InfoAsync("Não foi possível trocar o dono", r.Error.Trim() + hint);
        }

        await LoadRemoteAsync();
    }

    /// <summary>Sem editor (FTP): baixa uma cópia temporária e abre no programa padrão.</summary>
    private async Task OpenCopyAsync(RemoteEntry entry)
    {
        if (IsExecutable(entry.Name))
        {
            await Prompts.InfoAsync("Arquivo executável", $"\"{entry.Name}\" é um programa ou script: abrir aqui o executaria neste computador. Baixe pela seta ← se precisar dele.");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "RootDock", "copias", Guid.NewGuid().ToString("N")[..8]);
        var local = Path.Combine(folder, RemoteFilesViewModel.SafeLocalName(entry.Name));
        RemoteFooter.Text = $"Baixando {entry.Name}...";
        var fs = await _source.Connect();
        await fs.DownloadFileAsync(entry.FullPath, local, null, CancellationToken.None);
        RemoteFooter.Text = "Cópia aberta: alterações nela não voltam para o servidor (envie de volta pela seta →).";
        Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
