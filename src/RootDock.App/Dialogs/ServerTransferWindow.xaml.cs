using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.Core.ServerTransfer;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>
/// Transferência entre dois servidores: cada coluna é um servidor (sessão SSH salva ou aberta).
/// A cópia vai direto de um para o outro com rsync quando dá, ou passa pelo RootDock (SFTP dos
/// dois lados, sem gravar neste computador). Pastas inteiras, pausa, retomada e sincronização.
/// </summary>
public partial class ServerTransferWindow : FluentWindow
{
    private enum CopyMode
    {
        Automatic,
        Direct,
        Relay,
    }

    /// <summary>Uma linha da lista de arquivos.</summary>
    private sealed class Row
    {
        public Row(RemoteEntry entry)
        {
            Entry = entry;
            var look = new RemoteEntryViewModel(entry);
            Icon = look.Icon;
            IconBrush = look.IconBrush;
            SizeText = entry.IsDirectory ? "" : Format.Bytes(entry.Size);
            ModifiedText = entry.Modified == default ? "" : entry.Modified.ToString("dd/MM/yy HH:mm");
        }

        public RemoteEntry Entry { get; }

        public string Name => Entry.Name;

        public SymbolRegular Icon { get; }

        public Brush IconBrush { get; }

        public string SizeText { get; }

        public string ModifiedText { get; }

        public string Permissions => Entry.Permissions;

        public string ToolTip => Entry.FullPath;

        public double Opacity => Entry.IsHidden ? 0.65 : 1;

        public override string ToString() => Name; // leitores de tela e automação
    }

    /// <summary>Um servidor na caixa de escolha.</summary>
    private sealed record Choice(SessionEntry Entry, bool Open)
    {
        public override string ToString() =>
            (string.IsNullOrEmpty(Entry.FolderPath) ? "" : Entry.FolderPath + " / ") + Entry.Name +
            (string.Equals(Entry.Name, Entry.Host, StringComparison.OrdinalIgnoreCase) ? "" : $"  ·  {Entry.Host}") +
            (Entry.Environment == SessionEnvironment.Production ? "  (PRODUÇÃO)" : "") +
            (Open ? "  ·  aberta" : "");
    }

    /// <summary>Uma das duas colunas.</summary>
    private sealed class Side(string label, ComboBox server, Wpf.Ui.Controls.TextBox pathBox, ListBox list, TextBlock footer, TextBlock hint)
    {
        public string Label { get; } = label;

        public ComboBox Server { get; } = server;

        public Wpf.Ui.Controls.TextBox PathBox { get; } = pathBox;

        public ListBox List { get; } = list;

        public TextBlock Footer { get; } = footer;

        public TextBlock Hint { get; } = hint;

        public ServerLink? Link { get; set; }

        public string Path { get; set; } = "/";

        /// <summary>Pasta a abrir assim que conectar (quando a janela nasce de um terminal).</summary>
        public string? StartPath { get; set; }

        public int Version { get; set; }

        public Choice? Current { get; set; }

        public bool Restoring { get; set; }
    }

    private readonly ServerLinkFactory _links;
    private readonly TransferQueue _transfers;
    private readonly Side _left;
    private readonly Side _right;
    private readonly List<TransferItem> _mine = [];
    private readonly Dictionary<(ServerLink Source, ServerLink Target), Task<(DirectChannel? Channel, string? Reason)>> _channels = [];
    private bool _closing;

    private ServerTransferWindow(IEnumerable<SessionEntry> sessions, ServerLinkFactory links, TransferQueue transfers, SessionEntry? first, string? firstPath)
    {
        InitializeComponent();
        Title = "Servidor ↔ Servidor";
        _links = links;
        _transfers = transfers;
        _left = new Side("esquerda", LeftServer, LeftPath, LeftList, LeftFooter, LeftHint);
        _right = new Side("direita", RightServer, RightPath, RightList, RightFooter, RightHint);

        var open = links.OpenSessions();
        var choices = sessions.Where(s => s.Kind == SessionKind.Ssh)
            .OrderBy(s => s.FolderPath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new Choice(s, open.Contains(s.Id)))
            .ToList();
        if (first is not null && choices.All(c => c.Entry.Id != first.Id))
        {
            choices.Insert(0, new Choice(first, true)); // conexão rápida (não salva)
        }

        LeftServer.ItemsSource = choices;
        RightServer.ItemsSource = choices.ToList();
        QueueList.ItemsSource = _transfers.Items;
        _transfers.Items.CollectionChanged += OnQueueChanged;
        _transfers.Completed += OnTransferCompleted;
        UpdateQueueEmpty();
        Closing += OnClosing;
        Closed += OnClosed;
        Loaded += (_, _) =>
        {
            if (first is not null && choices.FirstOrDefault(c => c.Entry.Id == first.Id) is { } choice)
            {
                _left.StartPath = firstPath;
                LeftServer.SelectedItem = choice;
            }
        };
    }

    /// <summary>Abre a janela (não modal). <paramref name="first"/>: servidor que já entra na coluna da esquerda.</summary>
    public static void ShowFor(
        Window owner, IEnumerable<SessionEntry> sessions, ServerLinkFactory links, TransferQueue transfers,
        SessionEntry? first = null, string? firstPath = null) =>
        new ServerTransferWindow(sessions, links, transfers, first, firstPath) { Owner = owner }.Show();

    private Side SideOf(object sender) => (sender as FrameworkElement)?.Tag as string == "R" ? _right : _left;

    private Side Other(Side side) => side == _left ? _right : _left;

    private CopyMode Mode => ModeBox.SelectedIndex switch { 1 => CopyMode.Direct, 2 => CopyMode.Relay, _ => CopyMode.Automatic };

    // ------------------------------------------------------------------ servidores
    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        var side = SideOf(sender);
        if (side.Restoring || side.Server.SelectedItem is not Choice choice || ReferenceEquals(choice, side.Current))
        {
            return;
        }

        if (side.Link is { } old && _mine.Any(i => i.IsActive || i.State == TransferState.Paused))
        {
            side.Restoring = true;
            side.Server.SelectedItem = side.Current;
            side.Restoring = false;
            await Prompts.InfoAsync("Há transferências em andamento", "Espere terminarem (ou cancele) antes de trocar de servidor: elas usam esta conexão.");
            return;
        }

        var previous = side.Link;
        side.Link = null;
        side.Current = choice;
        var version = ++side.Version;
        side.List.ItemsSource = null;
        side.Footer.Text = "";
        Hint(side, $"Conectando a {choice.Entry.Name}...");
        if (previous is not null)
        {
            _ = ReleaseAsync(previous);
        }

        try
        {
            var link = await _links.ConnectAsync(choice.Entry, CancellationToken.None);
            if (_closing || version != side.Version)
            {
                link.Dispose();
                return;
            }

            side.Link = link;
            side.Path = side.StartPath is { Length: > 0 } start ? start : link.Files.HomeDirectory is { Length: > 0 } home ? home : "/";
            side.StartPath = null;
            await LoadAsync(side);
        }
        catch (Exception ex)
        {
            if (version == side.Version)
            {
                Log.Error($"Servidor ↔ Servidor: conectar {choice.Entry.Name}", ex);
                Hint(side, "Não foi possível conectar.\n" + Terminal.SessionConnector.Describe(ex));
            }
        }
    }

    /// <summary>Fecha um servidor que saiu da janela: antes tira as chaves temporárias que o usavam.</summary>
    private async Task ReleaseAsync(ServerLink link)
    {
        foreach (var key in _channels.Keys.Where(k => k.Source == link || k.Target == link).ToList())
        {
            var pending = _channels[key];
            _channels.Remove(key);
            await CloseChannelAsync(pending);
        }

        link.Dispose();
    }

    private static async Task CloseChannelAsync(Task<(DirectChannel? Channel, string? Reason)> pending)
    {
        try
        {
            if ((await pending).Channel is { } channel)
            {
                await channel.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Servidor ↔ Servidor: fechar o modo direto", ex);
        }
    }

    private static void Hint(Side side, string? text)
    {
        side.Hint.Text = text ?? "";
        side.Hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ pastas
    private async Task LoadAsync(Side side, string? select = null)
    {
        if (side.Link is not { } link)
        {
            return;
        }

        var version = ++side.Version;
        var path = side.Path;
        try
        {
            var entries = await link.Files.ListAsync(path, CancellationToken.None);
            if (version != side.Version)
            {
                return;
            }

            var rows = entries
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => new Row(x))
                .ToList();
            side.List.ItemsSource = rows;
            side.PathBox.Text = path;
            var folders = rows.Count(r => r.Entry.IsDirectory);
            side.Footer.Text = $"{link.User}@{link.Entry.Host}  ·  {folders} pasta(s), {rows.Count - folders} arquivo(s), {Format.Bytes(rows.Where(r => !r.Entry.IsDirectory).Sum(r => r.Entry.Size))}";
            Hint(side, rows.Count == 0 ? "Pasta vazia." : null);
            if (select is not null && rows.FirstOrDefault(r => r.Name == select) is { } row)
            {
                side.List.SelectedItem = row;
                side.List.ScrollIntoView(row);
            }
        }
        catch (Exception ex)
        {
            if (version == side.Version)
            {
                side.List.ItemsSource = null;
                side.PathBox.Text = path;
                Hint(side, "Não foi possível abrir a pasta.\n" + ex.Message);
            }
        }
    }

    private async Task GoAsync(Side side, string path, string? select = null)
    {
        side.Path = path.Length > 1 ? path.TrimEnd('/') : "/";
        await LoadAsync(side, select);
    }

    private async void OnUp(object sender, RoutedEventArgs e) => await UpAsync(SideOf(sender));

    private Task UpAsync(Side side) =>
        side.Path is "/" or "" ? Task.CompletedTask : GoAsync(side, RemotePath.Parent(side.Path), RemotePath.Name(side.Path));

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        var side = SideOf(sender);
        if (side.Link is { } link)
        {
            await GoAsync(side, link.Files.HomeDirectory is { Length: > 0 } home ? home : "/");
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync(SideOf(sender));

    private async void OnPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var side = SideOf(sender);
        var text = side.PathBox.Text.Trim();
        if (text.StartsWith('/'))
        {
            await GoAsync(side, text);
            side.List.Focus();
        }
    }

    private async void OnOpen(object sender, MouseButtonEventArgs e)
    {
        var side = SideOf(sender);
        if (side.List.SelectedItem is Row { Entry.IsDirectory: true } row)
        {
            await GoAsync(side, row.Entry.FullPath);
        }
    }

    private async void OnListKey(object sender, KeyEventArgs e)
    {
        var side = SideOf(sender);
        switch (e.Key)
        {
            case Key.Enter when side.List.SelectedItem is Row { Entry.IsDirectory: true } row:
                e.Handled = true;
                await GoAsync(side, row.Entry.FullPath);
                break;
            case Key.Back:
                e.Handled = true;
                await UpAsync(side);
                break;
            case Key.F5:
                e.Handled = true;
                await LoadAsync(side);
                break;
            case Key.F7:
                e.Handled = true;
                await NewFolderAsync(side);
                break;
        }
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e) => await NewFolderAsync(SideOf(sender));

    private async Task NewFolderAsync(Side side)
    {
        if (side.Link is not { } link)
        {
            return;
        }

        var name = await Prompts.AskTextAsync($"Nova pasta em {side.Path}", "Nome da pasta", "", "Criar");
        if (name is null)
        {
            return;
        }

        if (name.Contains('/') || name is "." or "..")
        {
            await Prompts.InfoAsync("Nome inválido", "O nome da pasta não pode ter \"/\".");
            return;
        }

        try
        {
            await link.Files.CreateDirectoryAsync(RemotePath.Combine(side.Path, name), CancellationToken.None);
            await LoadAsync(side, name);
        }
        catch (Exception ex)
        {
            await Prompts.InfoAsync("Não foi possível criar a pasta", ex.Message);
        }
    }

    // ------------------------------------------------------------------ copiar
    private async void OnCopyRight(object sender, RoutedEventArgs e) => await CopySelectedAsync(_left, _right);

    private async void OnCopyLeft(object sender, RoutedEventArgs e) => await CopySelectedAsync(_right, _left);

    private async Task<bool> ReadyAsync(Side from, Side to)
    {
        if (from.Link is null || to.Link is null)
        {
            await Prompts.InfoAsync("Escolha os dois servidores", "Conecte um servidor em cada coluna antes de copiar.");
            return false;
        }

        if (from.Link.Entry.Id == to.Link.Entry.Id && from.Path == to.Path)
        {
            await Prompts.InfoAsync("Mesma pasta", "As duas colunas estão na mesma pasta do mesmo servidor.");
            return false;
        }

        return true;
    }

    private async Task CopySelectedAsync(Side from, Side to)
    {
        if (!await ReadyAsync(from, to))
        {
            return;
        }

        var rows = from.List.SelectedItems.Cast<Row>().ToList();
        if (rows.Count == 0)
        {
            await Prompts.InfoAsync("Nada selecionado", $"Selecione arquivos ou pastas na coluna da {from.Label}.");
            return;
        }

        var source = from.Link!;
        var target = to.Link!;
        if (source.Entry.Id == target.Entry.Id && rows.Any(r => r.Entry.IsDirectory && (to.Path + "/").StartsWith(r.Entry.FullPath + "/", StringComparison.Ordinal)))
        {
            await Prompts.InfoAsync("Destino dentro da origem", "Não dá para copiar uma pasta para dentro dela mesma.");
            return;
        }

        var skip = SkipBox.IsChecked == true;
        var existing = (to.List.ItemsSource as IEnumerable<Row>)?.Select(r => r.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var conflicts = rows.Where(r => existing.Contains(r.Name)).Select(r => r.Name).ToList();
        if (conflicts.Count > 0 && !skip)
        {
            var list = string.Join("\n", conflicts.Take(10)) + (conflicts.Count > 10 ? $"\n... e mais {conflicts.Count - 10}" : "");
            var production = target.IsProduction ? "\n\nATENÇÃO: o destino é de PRODUÇÃO." : "";
            if (!await Prompts.ConfirmAsync(
                    $"{conflicts.Count} item(ns) já existem em {target.Entry.Name}",
                    $"Arquivos com o mesmo nome serão substituídos pela versão de {source.Entry.Name}; pastas com o mesmo nome são mescladas (nada é apagado). " +
                    $"Para manter o que já existe, marque \"Não substituir o que já existe\".{production}\n\n{list}",
                    "Substituir", danger: true))
            {
                return;
            }
        }
        else if (target.IsProduction && !await Prompts.ConfirmAsync(
                     $"Copiar para {target.Entry.Name} (PRODUÇÃO)?",
                     $"{rows.Count} item(ns) de {source.Entry.Name}:{from.Path} para {to.Path}.", "Copiar", danger: true))
        {
            return;
        }

        var items = rows.Select(r => new CopyItem(r.Name, r.Entry.IsDirectory && !r.Entry.IsSymlink, r.Entry.Size)).ToList();
        var request = new CopyRequest(from.Path, to.Path, items) { Preserve = PreserveBox.IsChecked == true, SkipExisting = skip };
        var name = rows.Count == 1 ? rows[0].Name + (rows[0].Entry.IsDirectory ? "/" : "") : $"{rows.Count} itens de {FolderName(from.Path)}";
        Enqueue(name, request, source, target, items.Where(i => !i.IsDirectory).Sum(i => i.Size));
    }

    private void Enqueue(string name, CopyRequest request, ServerLink source, ServerLink target, long knownBytes)
    {
        var mode = Mode;
        var relay = new ServerRelay(source.Files, target.Files, request);
        var usedRelay = false;
        var item = _transfers.Enqueue(name, upload: true, knownBytes, async transfer =>
        {
            DirectChannel? direct = null;
            if (mode != CopyMode.Relay && !usedRelay)
            {
                string? reason;
                while (true)
                {
                    try
                    {
                        (direct, reason) = await GetChannelAsync(source, target, transfer.Token);
                        break;
                    }
                    catch (OperationCanceledException) when (!transfer.Token.IsCancellationRequested)
                    {
                        // Outra transferência que estava preparando o caminho foi cancelada: prepara de novo.
                    }
                }

                if (direct is null && mode == CopyMode.Direct)
                {
                    throw new InvalidOperationException("Modo direto indisponível: " + reason + ".");
                }

                ShowMode(direct is not null
                    ? $"Direto entre {source.Entry.Name} e {target.Entry.Name} (rsync): os dados não passam por este computador."
                    : $"Passando pelo RootDock (SFTP): {reason}.");
            }
            else
            {
                ShowMode("Passando pelo RootDock (SFTP).");
            }

            if (direct is not null)
            {
                await TransferJobs.EnsureDirectoryAsync(target.Files, request.TargetRoot, transfer.Token);
                await direct.CopyAsync(request,
                    (path, content, ct) => source.Files.WriteAllBytesAsync(path, content, ct),
                    total => transfer.TotalBytes = total, transfer.SetDone, transfer.Token);
                if (transfer.TotalBytes == 0)
                {
                    transfer.TotalBytes = transfer.Done;
                }

                transfer.SetDone(transfer.TotalBytes); // o rsync só conta o que precisou copiar
                return;
            }

            usedRelay = true; // retomar continua pelo mesmo caminho (o parcial é dele)
            var summary = await relay.RunAsync(transfer.Attempt > 1, total => transfer.TotalBytes = total, transfer.SetDone,
                () => transfer.PauseRequested, transfer.Token);
            if (summary.SkippedLinks > 0 || summary.Skipped > 0)
            {
                var notes = new List<string>();
                if (summary.Skipped > 0)
                {
                    notes.Add($"{summary.Skipped} arquivo(s) já existiam e ficaram como estavam");
                }

                if (summary.SkippedLinks > 0)
                {
                    notes.Add($"{summary.SkippedLinks} link(s) simbólico(s) não foram copiados (o modo direto copia)");
                }

                ShowMode($"{name}: {string.Join("; ", notes)}.");
            }
        }, $"{source.Entry.Name} → {target.Entry.Name}", glyph: "⇄");
        _mine.Add(item);
    }

    private static string FolderName(string path) => RemotePath.Name(path) is { Length: > 0 } name ? name : "/";

    private void ShowMode(string text) => Dispatcher.BeginInvoke(() => ModeStatus.Text = text);

    /// <summary>
    /// O caminho direto entre dois servidores, preparado uma vez por par e sentido. Não deu:
    /// devolve o motivo (e a cópia segue pelo RootDock), sem tentar de novo a cada arquivo.
    /// </summary>
    private Task<(DirectChannel? Channel, string? Reason)> GetChannelAsync(ServerLink source, ServerLink target, CancellationToken ct)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue((source, target), out var pending) || pending.IsCanceled)
            {
                pending = OpenChannelAsync(source, target, ct);
                _channels[(source, target)] = pending;
            }

            return pending;
        }
    }

    private async Task<(DirectChannel? Channel, string? Reason)> OpenChannelAsync(ServerLink source, ServerLink target, CancellationToken ct)
    {
        ShowMode($"Preparando a cópia direta entre {source.Entry.Name} e {target.Entry.Name}...");
        try
        {
            var from = await source.GetCommandsAsync(ct);
            var to = await target.GetCommandsAsync(ct);
            var endpoint = new DirectEndpoint(target.Entry.Host, target.Entry.Port, target.User, _links.KnownHostLines(target.Entry));
            return (await Task.Run(() => DirectChannel.OpenAsync(from, to, endpoint, ct), ct), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectUnavailableException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Servidor ↔ Servidor: preparar o modo direto", ex);
            return (null, "não foi possível preparar a cópia direta (" + ex.Message + ")");
        }
    }

    // ------------------------------------------------------------------ sincronizar
    private async void OnSyncRight(object sender, RoutedEventArgs e) => await SyncAsync(_left, _right);

    private async void OnSyncLeft(object sender, RoutedEventArgs e) => await SyncAsync(_right, _left);

    private async Task SyncAsync(Side from, Side to)
    {
        if (!await ReadyAsync(from, to))
        {
            return;
        }

        var source = from.Link!;
        var target = to.Link!;
        var sourceRoot = from.Path;
        var targetRoot = to.Path;
        List<CopyItem> plan;
        var notes = new List<string>();
        ModeStatus.Text = "Comparando as duas pastas (com subpastas)...";
        try
        {
            var scanSource = RemoteTree.ScanAsync(source.Files, sourceRoot, CancellationToken.None);
            var scanTarget = RemoteTree.ScanAsync(target.Files, targetRoot, CancellationToken.None);
            var there = await scanSource;
            var here = await scanTarget;
            if (there.Truncated || here.Truncated)
            {
                ModeStatus.Text = "";
                await Prompts.InfoAsync("Pasta grande demais para comparar",
                    $"Uma das pastas passa de {RemoteTree.MaxFiles:N0} arquivos. Sincronize por subpastas, ou copie a pasta inteira no modo direto (o rsync já pula o que está igual).");
                return;
            }

            plan = there.Files
                .Where(f => !here.Files.TryGetValue(f.Key, out var other) || other.Size != f.Value.Size || f.Value.Modified > other.Modified.AddSeconds(2))
                .Select(f => new CopyItem(f.Key, false, f.Value.Size))
                .OrderBy(i => i.RelativePath, StringComparer.Ordinal)
                .ToList();
            if (there.SkippedLinks.Count > 0)
            {
                notes.Add($"{there.SkippedLinks.Count} link(s) simbólico(s) da origem ficaram de fora.");
            }
        }
        catch (Exception ex)
        {
            ModeStatus.Text = "";
            await Prompts.InfoAsync("Não foi possível comparar", ex.Message);
            return;
        }

        ModeStatus.Text = string.Join(" ", notes);
        if (plan.Count == 0)
        {
            await Prompts.InfoAsync("Tudo sincronizado", $"{target.Entry.Name}:{targetRoot} já tem tudo o que há em {source.Entry.Name}:{sourceRoot}.");
            return;
        }

        var total = plan.Sum(p => p.Size);
        var list = string.Join("\n", plan.Take(12).Select(p => $"{p.RelativePath}  ({Format.Bytes(p.Size)})"));
        var production = target.IsProduction ? "\n\nATENÇÃO: o destino é de PRODUÇÃO." : "";
        var extra = notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "";
        if (!await Prompts.ConfirmAsync(
                $"Copiar {plan.Count} arquivo(s) para {target.Entry.Name}?",
                $"{Format.Bytes(total)} no total, de {source.Entry.Name}:{sourceRoot} para {targetRoot}. " +
                $"Arquivos com o mesmo nome no destino serão atualizados; nada será apagado.{production}{extra}\n\n{list}{(plan.Count > 12 ? $"\n... e mais {plan.Count - 12}" : "")}",
                "Sincronizar", danger: target.IsProduction))
        {
            return;
        }

        var request = new CopyRequest(sourceRoot, targetRoot, plan) { Preserve = PreserveBox.IsChecked == true };
        Enqueue($"Sincronizar {FolderName(sourceRoot)} ({plan.Count} arquivos)", request, source, target, total);
    }

    // ------------------------------------------------------------------ fila e fechamento
    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateQueueEmpty();

    private void UpdateQueueEmpty() => QueueEmpty.Visibility = _transfers.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnClearFinished(object sender, RoutedEventArgs e) => _transfers.ClearFinishedCommand.Execute(null);

    private void OnTransferCompleted(TransferItem item)
    {
        if (!_mine.Contains(item))
        {
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            if (!_closing)
            {
                await LoadAsync(_left);
                await LoadAsync(_right);
            }
        });
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        var busy = _mine.Where(i => i.IsActive || i.State == TransferState.Paused).ToList();
        if (busy.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        if (await Prompts.ConfirmAsync("Fechar e cancelar as transferências?",
                $"{busy.Count} transferência(s) entre servidores ainda não terminaram. Fechar a janela cancela todas; o que já chegou completo ao destino fica lá.",
                "Fechar e cancelar", danger: true))
        {
            foreach (var item in busy)
            {
                item.CancelCommand.Execute(null);
            }

            _closing = true;
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _transfers.Items.CollectionChanged -= OnQueueChanged;
        _transfers.Completed -= OnTransferCompleted;
        var mine = _mine.ToList();
        var channels = _channels.Values.ToList();
        var links = new[] { _left.Link, _right.Link }.Where(l => l is not null).Cast<ServerLink>().ToList();
        _channels.Clear();
        _left.Link = null;
        _right.Link = null;

        // Em segundo plano: espera os cancelamentos pararem o rsync, tira as chaves temporárias e só então desconecta.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 30 && mine.Any(m => m.State == TransferState.Running); i++)
            {
                await Task.Delay(100);
            }

            foreach (var pending in channels)
            {
                await CloseChannelAsync(pending);
            }

            foreach (var link in links)
            {
                link.Dispose();
            }
        });
    }
}
