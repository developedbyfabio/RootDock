using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.MultiExec;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Settings;
using RootDock.Core.Ssh;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>
/// Executa o mesmo comando em vários servidores (SSH), com resultado individual, execução em
/// lotes, intervalo, tempo limite, cancelamento, logs por servidor e trava para comandos perigosos.
/// </summary>
public partial class MultiExecWindow : FluentWindow
{
    private const string ConfirmWord = "EXECUTAR";
    private const int MaxHistory = 15;

    private static readonly Brush Green = Hex("#9ECE6A");
    private static readonly Brush Red = Hex("#F7768E");
    private static readonly Brush Yellow = Hex("#E0AF68");
    private static readonly Brush Blue = Hex("#7AA2F7");
    private static readonly Brush Dim = Hex("#8B93A5");

    private readonly ObservableCollection<TargetItem> _targets = new();
    private readonly ObservableCollection<ResultItem> _results = new();
    private readonly TerminalWorkspace _workspace;
    private readonly KnownHostsStore _knownHosts;
    private readonly CredentialVault _vault;
    private readonly SessionLogins _logins;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly string _logsRoot;
    private CancellationTokenSource? _run;
    private string? _lastLogFolder;
    private string _lastCommand = "";

    private MultiExecWindow(
        IEnumerable<SessionEntry> sessions, IEnumerable<Guid> preselected, TerminalWorkspace workspace, KnownHostsStore knownHosts,
        CredentialVault vault, AppSettings settings, Action saveSettings, string dataDirectory)
    {
        InitializeComponent();
        _workspace = workspace;
        _knownHosts = knownHosts;
        _vault = vault;
        _logins = new SessionLogins(vault);
        _settings = settings;
        _saveSettings = saveSettings;
        _logsRoot = Path.Combine(dataDirectory, "multiexec");

        var picked = preselected.ToHashSet();
        var open = OpenClients().Keys.ToHashSet();
        foreach (var entry in sessions.Where(s => s.Kind == SessionKind.Ssh)
                     .OrderBy(s => s.FolderPath, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new TargetItem(entry, open.Contains(entry.Id)) { IsSelected = picked.Contains(entry.Id) };
            item.PropertyChanged += (_, _) => UpdateState();
            _targets.Add(item);
        }

        TargetsList.ItemsSource = _targets;
        ResultsList.ItemsSource = _results;
        HistoryBox.ItemsSource = settings.MultiExecHistory.ToList();
        ConcurrencyBox.Value = Math.Clamp(settings.MultiExecConcurrency, 1, 50);
        TimeoutBox.Value = Math.Clamp(settings.MultiExecTimeoutSeconds, 5, 86400);
        UpdateState();
        Loaded += (_, _) => CommandBox.Focus();
        Closing += OnClosing;
    }

    /// <summary>Abre a janela (não modal: dá para continuar usando os terminais).</summary>
    public static void Show(
        Window owner, IEnumerable<SessionEntry> sessions, IEnumerable<Guid> preselected, TerminalWorkspace workspace,
        KnownHostsStore knownHosts, CredentialVault vault, AppSettings settings, Action saveSettings, string dataDirectory, string? command = null)
    {
        var window = new MultiExecWindow(sessions, preselected, workspace, knownHosts, vault, settings, saveSettings, dataDirectory) { Owner = owner };
        if (!string.IsNullOrWhiteSpace(command))
        {
            window.CommandBox.Text = command;
        }

        window.Show();
    }

    // ------------------------------------------------------------------ seleção
    private IEnumerable<TargetItem> Selected => _targets.Where(t => t.IsSelected);

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        TargetsList.Items.Filter = text.Length == 0 ? null : o => SessionManager.Matches(((TargetItem)o).Entry, text);
    }

    private void OnSelectVisible(object sender, RoutedEventArgs e)
    {
        foreach (TargetItem item in TargetsList.Items)
        {
            item.IsSelected = true;
        }
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in _targets)
        {
            item.IsSelected = false;
        }
    }

    private void OnSelectOpen(object sender, RoutedEventArgs e)
    {
        foreach (var item in _targets)
        {
            item.IsSelected = item.IsOpen;
        }
    }

    private void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryBox.SelectedItem is string command)
        {
            CommandBox.Text = command;
            CommandBox.Focus();
            CommandBox.CaretIndex = command.Length;
        }
    }

    private string _confirmedFor = "";

    private void OnCommandChanged(object sender, TextChangedEventArgs e)
    {
        // A confirmação "EXECUTAR" vale só para o comando que estava escrito quando foi digitada.
        if (ConfirmBox is not null && ConfirmBox.Text.Length > 0 && CommandBox.Text.Trim() != _confirmedFor)
        {
            ConfirmBox.Text = "";
        }

        UpdateState();
    }

    private void OnConfirmChanged(object sender, TextChangedEventArgs e)
    {
        _confirmedFor = CommandBox.Text.Trim();
        UpdateState();
    }

    /// <summary>Contagem, botão, trava de comando perigoso e aviso de produção.</summary>
    private void UpdateState()
    {
        if (!IsInitialized || RunButton is null)
        {
            return;
        }

        var selected = Selected.ToList();
        var running = _run is not null;
        SelectionText.Text = $"{selected.Count} de {_targets.Count} marcados";
        var command = CommandBox.Text.Trim();
        var risks = CommandSafety.Analyze(command);
        DangerPanel.Visibility = risks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (risks.Count > 0)
        {
            DangerTitle.Text = selected.Count > 1
                ? $"Comando perigoso para rodar em {selected.Count} servidores ao mesmo tempo"
                : "Comando perigoso";
            DangerReasons.Text = "• " + string.Join("\n• ", risks);
        }

        var production = selected.Count(t => t.Entry.Environment == SessionEnvironment.Production);
        ProductionNote.Visibility = production > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProductionNote.Text = production == 1
            ? "1 servidor de PRODUÇÃO marcado: será pedida confirmação."
            : $"{production} servidores de PRODUÇÃO marcados: será pedida confirmação.";

        var unlocked = risks.Count == 0 || string.Equals(ConfirmBox.Text.Trim(), ConfirmWord, StringComparison.Ordinal);
        RunButton.Content = selected.Count == 1 ? "Executar em 1 servidor" : $"Executar em {selected.Count} servidores";
        RunButton.Appearance = risks.Count > 0 ? ControlAppearance.Danger : ControlAppearance.Primary;
        RunButton.IsEnabled = !running && selected.Count > 0 && command.Length > 0 && unlocked;
        RunButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelRunButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunHint.Text = running ? ""
            : selected.Count == 0 ? "Marque pelo menos um servidor."
            : command.Length == 0 ? "Escreva o comando."
            : !unlocked ? $"Digite {ConfirmWord} para liberar."
            : "";
    }

    // ------------------------------------------------------------------ execução
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text.Trim();
        var selected = Selected.ToList();
        if (command.Length == 0 || selected.Count == 0 || _run is not null)
        {
            return;
        }

        var production = selected.Where(t => t.Entry.Environment == SessionEnvironment.Production).ToList();
        if (production.Count > 0)
        {
            var names = string.Join(", ", production.Take(8).Select(t => t.Entry.Name)) + (production.Count > 8 ? $" e mais {production.Count - 8}" : "");
            if (!await Prompts.ConfirmAsync(
                    "Executar em PRODUÇÃO?",
                    $"O comando vai rodar em {production.Count} servidor(es) de produção: {names}.\n\n{command}",
                    "Executar", danger: true))
            {
                return;
            }
        }

        Remember(command);
        // O que fica em disco e na tela (logs, relatório, histórico) vai com senhas mascaradas.
        _lastCommand = Core.History.HistorySanitizer.Clean(command) ?? "(comando começando com espaço: não registrado)";
        ConfirmBox.Text = ""; // a próxima execução de comando perigoso pede de novo
        var options = new MultiExecOptions
        {
            Concurrency = (int)(ConcurrencyBox.Value ?? 5),
            Interval = TimeSpan.FromSeconds(IntervalBox.Value ?? 0),
            Timeout = TimeSpan.FromSeconds(TimeoutBox.Value ?? 120),
            StopOnError = StopOnErrorBox.IsChecked == true,
        };
        _settings.MultiExecConcurrency = options.Concurrency;
        _settings.MultiExecTimeoutSeconds = (int)options.Timeout.TotalSeconds;
        _saveSettings();

        _results.Clear();
        var rows = new Dictionary<string, ResultItem>();
        var targets = new List<MultiExecTarget>();
        var sharedPassword = SharedPasswordBox.Password.Length > 0 ? SharedPasswordBox.Password : null;
        var openClients = OpenClients();
        foreach (var item in selected)
        {
            var entry = item.Entry;
            var row = new ResultItem(entry.Name, entry.Endpoint);
            rows[entry.Id.ToString("N")] = row;
            _results.Add(row);
            targets.Add(new MultiExecTarget(entry.Id.ToString("N"), entry.Name, ct => ConnectAsync(entry, openClients, sharedPassword, ct)));
        }

        EmptyResults.Visibility = Visibility.Collapsed;
        ResultsList.SelectedIndex = 0;
        _run = new CancellationTokenSource();
        UpdateState();
        SummaryText.Text = "Executando...";
        var watch = Stopwatch.StartNew();
        try
        {
            await MultiExecRunner.RunAsync(targets, command, options, progress => Dispatcher.BeginInvoke(() =>
            {
                if (rows.TryGetValue(progress.Target.Id, out var row))
                {
                    row.Apply(progress);
                    if (ResultsList.SelectedItem == row)
                    {
                        ShowOutput(row);
                    }

                    UpdateSummary(running: true, watch.Elapsed);
                }
            }), _run.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Execução em vários servidores", ex);
        }
        finally
        {
            _run.Dispose();
            _run = null;
        }

        // As últimas atualizações de linha ainda estão na fila da interface.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        UpdateSummary(running: false, watch.Elapsed);
        UpdateState();
        SaveLogs(_lastCommand, watch.Elapsed);
        if (ResultsList.SelectedItem is ResultItem current)
        {
            ShowOutput(current);
        }
    }

    /// <summary>Terminal aberto e conectado: usa a conexão dele. Senão, conecta com o login salvo.</summary>
    private async Task<ICommandExecutor> ConnectAsync(SessionEntry entry, IReadOnlyDictionary<Guid, Renci.SshNet.SshClient> open, string? sharedPassword, CancellationToken ct)
    {
        if (open.TryGetValue(entry.Id, out var client) && client.IsConnected)
        {
            return SshCommandExecutor.Borrow(client);
        }

        if (entry.JumpSessionId is not null)
        {
            throw new InvalidOperationException("Sessão com bastion (ProxyJump): abra o terminal dela antes; a execução usa a conexão aberta.");
        }

        var user = entry.Username;
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("Sessão sem usuário salvo: abra uma vez pelo terminal e salve o login.");
        }

        string? password = null;
        string? passphrase = null;
        try
        {
            password = _logins.ReadPassword(entry, user);
            if (entry.PrivateKeyPath is not null)
            {
                passphrase = _vault.Read(CredentialVault.KeyPassphraseKey(entry.Id));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Execução em vários: ler cofre", ex);
        }

        password ??= entry.PrivateKeyPath is null ? sharedPassword : null;
        if (password is null && entry.PrivateKeyPath is null && !entry.UseAgent)
        {
            throw new InvalidOperationException("Sem senha salva nem chave SSH: salve o login (botão direito → Logins salvos) ou informe a senha no campo acima.");
        }

        var credentials = new SshCredentials(user, password, entry.PrivateKeyPath, passphrase, entry.UseAgent);
        return await SshCommandExecutor.ConnectAsync(entry.Host, entry.Port, credentials, _knownHosts, ct);
    }

    /// <summary>Conexões dos terminais abertos agora (por sessão).</summary>
    private Dictionary<Guid, Renci.SshNet.SshClient> OpenClients()
    {
        var map = new Dictionary<Guid, Renci.SshNet.SshClient>();
        foreach (var session in _workspace.Sessions)
        {
            if (session.State == TerminalState.Connected && session.SshClient is { IsConnected: true } client && !map.ContainsKey(session.Entry.Id))
            {
                map[session.Entry.Id] = client;
            }
        }

        return map;
    }

    private void OnCancelRun(object sender, RoutedEventArgs e)
    {
        _run?.Cancel();
        SummaryText.Text = "Cancelando...";
    }

    private void UpdateSummary(bool running, TimeSpan elapsed)
    {
        int Count(params MultiExecStatus[] statuses) => _results.Count(r => statuses.Contains(r.Status));
        var parts = new List<string>();
        void Add(int n, string text)
        {
            if (n > 0)
            {
                parts.Add($"{n} {text}");
            }
        }

        Add(Count(MultiExecStatus.Succeeded), "ok");
        Add(Count(MultiExecStatus.Failed), "com erro");
        Add(Count(MultiExecStatus.TimedOut), "tempo esgotado");
        Add(Count(MultiExecStatus.Unreachable), "sem conexão");
        Add(Count(MultiExecStatus.Cancelled), "cancelados");
        if (running)
        {
            Add(Count(MultiExecStatus.Connecting, MultiExecStatus.Running), "em andamento");
            Add(Count(MultiExecStatus.Waiting), "na fila");
        }

        SummaryText.Text = (running ? "Executando: " : "Concluído: ") + string.Join(" · ", parts) + $"  —  {elapsed.TotalSeconds:0.0} s";
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultItem row)
        {
            ShowOutput(row);
        }
    }

    private void ShowOutput(ResultItem row)
    {
        OutputTitle.Text = $"Saída · {row.Name}  ({row.Endpoint})";
        OutputBox.Text = row.FullText(_lastCommand);
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length > 0)
        {
            Clipboard.SetText(OutputBox.Text);
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        if (_results.Count > 0)
        {
            Clipboard.SetText(Report());
        }
    }

    private string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comando: {_lastCommand}");
        sb.AppendLine(SummaryText.Text);
        sb.AppendLine();
        foreach (var row in _results)
        {
            sb.AppendLine($"===== {row.Name} ({row.Endpoint}) — {row.StatusText} {row.ExitCode} {row.Duration}".TrimEnd());
            sb.AppendLine(row.FullText(null).TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Um arquivo por servidor e o resumo, numa pasta por execução.</summary>
    private void SaveLogs(string command, TimeSpan elapsed)
    {
        try
        {
            var folder = Path.Combine(_logsRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(folder);
            foreach (var row in _results)
            {
                var name = string.Concat(row.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                File.WriteAllText(Path.Combine(folder, name + ".log"), row.FullText(command), new UTF8Encoding(false));
            }

            File.WriteAllText(Path.Combine(folder, "resumo.txt"), Report(), new UTF8Encoding(false));
            _lastLogFolder = folder;
            LogsButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Execução em vários: gravar logs", ex);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        if (_lastLogFolder is not null && Directory.Exists(_lastLogFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastLogFolder}\"") { UseShellExecute = true });
        }
    }

    private void Remember(string raw)
    {
        if (Core.History.HistorySanitizer.Clean(raw) is not { } command || command != raw)
        {
            return; // com senha/token (ou começando com espaço): não vai para a lista de recentes
        }

        var history = _settings.MultiExecHistory;
        history.RemoveAll(c => c == command);
        history.Insert(0, command);
        if (history.Count > MaxHistory)
        {
            history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        }

        HistoryBox.SelectionChanged -= OnHistoryPicked;
        HistoryBox.ItemsSource = history.ToList();
        HistoryBox.SelectionChanged += OnHistoryPicked;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_run is null)
        {
            return;
        }

        e.Cancel = true;
        if (await Prompts.ConfirmAsync("Execução em andamento", "Cancelar a execução nos servidores e fechar?", "Cancelar e fechar", danger: true))
        {
            _run?.Cancel();
            while (_run is not null)
            {
                await Task.Delay(100);
            }

            _ = Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static Brush Hex(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ itens
    private sealed class TargetItem(SessionEntry entry, bool isOpen) : INotifyPropertyChanged
    {
        private bool _selected;

        public SessionEntry Entry { get; } = entry;

        public bool IsOpen { get; } = isOpen;

        public string Name => Entry.Name;

        public string Endpoint => Entry.Endpoint;

        public string Tooltip => string.IsNullOrEmpty(Entry.FolderPath) ? Entry.Endpoint : $"{Entry.FolderPath} · {Entry.Endpoint}";

        public Visibility OpenVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

        public override string ToString() => Name;

        public Brush EnvBrush => Entry.Environment switch
        {
            SessionEnvironment.Production => Red,
            SessionEnvironment.Staging => Yellow,
            SessionEnvironment.Development => Green,
            _ => Hex("#3B4252"),
        };

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected != value)
                {
                    _selected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ResultItem(string name, string endpoint) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public string Endpoint { get; } = endpoint;

        public MultiExecStatus Status { get; private set; } = MultiExecStatus.Waiting;

        public string StatusText { get; private set; } = "Na fila";

        public Brush StatusBrush { get; private set; } = Dim;

        public string Duration { get; private set; } = "";

        public string ExitCode { get; private set; } = "";

        public string Summary { get; private set; } = "";

        private CommandResult? _result;
        private string? _message;

        public void Apply(MultiExecProgress progress)
        {
            Status = progress.Status;
            (StatusText, StatusBrush) = progress.Status switch
            {
                MultiExecStatus.Waiting => ("Na fila", Dim),
                MultiExecStatus.Connecting => ("Conectando", Blue),
                MultiExecStatus.Running => ("Executando", Yellow),
                MultiExecStatus.Succeeded => ("OK", Green),
                MultiExecStatus.Failed => ("Erro", Red),
                MultiExecStatus.TimedOut => ("Tempo esgotado", Red),
                MultiExecStatus.Cancelled => ("Cancelado", Dim),
                MultiExecStatus.Unreachable => ("Sem conexão", Red),
                _ => (progress.Status.ToString(), Dim),
            };
            _result = progress.Result ?? _result;
            _message = progress.Message;
            if (progress.Elapsed > TimeSpan.Zero && progress.Status is not (MultiExecStatus.Connecting or MultiExecStatus.Running))
            {
                Duration = progress.Elapsed.TotalSeconds < 10 ? $"{progress.Elapsed.TotalSeconds:0.0} s" : $"{progress.Elapsed.TotalSeconds:0} s";
            }

            ExitCode = _result?.ExitCode?.ToString() ?? "";
            Summary = FirstLine(_result) ?? _message ?? "";
            foreach (var name in new[] { nameof(Status), nameof(StatusText), nameof(StatusBrush), nameof(Duration), nameof(ExitCode), nameof(Summary) })
            {
                Changed(name);
            }
        }

        private static string? FirstLine(CommandResult? result)
        {
            if (result is null)
            {
                return null;
            }

            var text = result.Output.Trim().Length > 0 ? result.Output : result.Error;
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length switch
            {
                0 => "(sem saída)",
                1 => lines[0],
                _ => $"{lines[0]}  (+{lines.Length - 1} linhas)",
            };
        }

        public string FullText(string? command)
        {
            var sb = new StringBuilder();
            if (command is not null)
            {
                sb.AppendLine($"$ {command}");
            }

            if (_result is not null)
            {
                sb.Append(_result.Output);
                if (_result.Error.Trim().Length > 0)
                {
                    if (_result.Output.Length > 0 && !_result.Output.EndsWith('\n'))
                    {
                        sb.AppendLine();
                    }

                    sb.AppendLine("--- erros (stderr) ---");
                    sb.Append(_result.Error);
                }
            }

            if (_message is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"[{StatusText}] {_message}");
            }
            else if (_result is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"[{StatusText} · código {_result.ExitCode?.ToString() ?? "?"} · {Duration}]");
            }
            else if (Status is MultiExecStatus.Waiting or MultiExecStatus.Connecting or MultiExecStatus.Running)
            {
                sb.AppendLine($"[{StatusText}...]");
            }

            return sb.ToString();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
