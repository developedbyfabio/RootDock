using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.History;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>
/// Busca em todos os comandos já digitados nos terminais (de todas as sessões), com colar no
/// terminal ativo, copiar e executar em vários servidores.
/// </summary>
public partial class HistoryWindow : FluentWindow
{
    private static HistoryWindow? _open;

    private readonly CommandHistory _history;
    private readonly TerminalWorkspace _workspace;
    private readonly Action<string> _multiExec;
    private readonly Guid? _activeSession;

    private sealed record Item(HistoryEntry Entry)
    {
        public string Command => Entry.Command;

        public string Session => Entry.SessionName;

        public string When => Entry.Time.LocalDateTime.Date == DateTime.Today
            ? Entry.Time.LocalDateTime.ToString("HH:mm")
            : Entry.Time.LocalDateTime.ToString("dd/MM HH:mm");

        public string Tooltip => $"{Entry.Command}\n\n{Entry.SessionName} ({Entry.Host}) · {Entry.Time.LocalDateTime:dd/MM/yyyy HH:mm:ss}";
    }

    private HistoryWindow(CommandHistory history, TerminalWorkspace workspace, Action<string> multiExec)
    {
        InitializeComponent();
        _history = history;
        _workspace = workspace;
        _multiExec = multiExec;
        _activeSession = workspace.ActiveSession?.Entry.Id;
        var scopes = new List<string> { "Todas as sessões" };
        if (workspace.ActiveSession is { } active)
        {
            scopes.Add($"Só {active.Entry.Name}");
        }

        ScopeBox.ItemsSource = scopes;
        ScopeBox.SelectedIndex = 0;
        Loaded += (_, _) => SearchBox.Focus();
        Closed += (_, _) => _open = null;
        Refresh();
    }

    /// <summary>Abre (ou traz para a frente) a janela de histórico.</summary>
    public static void ShowFor(Window owner, CommandHistory history, TerminalWorkspace workspace, Action<string> multiExec)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            existing.SearchBox.Focus();
            existing.Refresh();
            return;
        }

        _open = new HistoryWindow(history, workspace, multiExec) { Owner = owner };
        _open.Show();
    }

    private void Refresh()
    {
        if (!IsInitialized || List is null)
        {
            return;
        }

        var scope = ScopeBox.SelectedIndex == 1 ? _activeSession : null;
        var items = _history.Search(SearchBox.Text, scope).Select(e => new Item(e)).ToList();
        List.ItemsSource = items;
        if (items.Count > 0)
        {
            List.SelectedIndex = 0;
        }

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _history.Count == 0
            ? "Ainda não há comandos. Tudo o que você executar nos terminais aparece aqui para buscar depois.\n\nSenhas e tokens no comando são mascarados; comandos que começam com espaço não são guardados."
            : "Nenhum comando com essas palavras.";
        CountText.Text = items.Count >= 500 ? "500+ resultados (refine a busca)" : $"{items.Count} comando(s)";
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Refresh();

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private string? Selected => (List.SelectedItem as Item)?.Command;

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when List.Items.Count > 0:
                List.SelectedIndex = Math.Min(List.Items.Count - 1, List.SelectedIndex + 1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when List.Items.Count > 0:
                List.SelectedIndex = Math.Max(0, List.SelectedIndex - 1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Paste();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Paste();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Copy();
            e.Handled = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Paste();

    private void OnPaste(object sender, RoutedEventArgs e) => Paste();

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    private void Copy()
    {
        if (Selected is { } command)
        {
            Clipboard.SetText(command);
        }
    }

    /// <summary>Coloca no terminal ativo sem executar (a pessoa confere e aperta Enter).</summary>
    private void Paste()
    {
        if (Selected is not { } command)
        {
            return;
        }

        if (_workspace.ActiveSession is not { } session)
        {
            _ = Prompts.InfoAsync("Nenhum terminal ativo", "Abra ou clique num terminal para colar o comando nele.");
            return;
        }

        _workspace.PasteInto(session, command);
        Owner?.Activate();
        _workspace.FocusTerminal();
    }

    private void OnMultiExec(object sender, RoutedEventArgs e)
    {
        if (Selected is { } command)
        {
            _multiExec(command);
        }
    }

    private async void OnClear(object sender, RoutedEventArgs e)
    {
        var onlyActive = ScopeBox.SelectedIndex == 1 && _activeSession is not null;
        var text = onlyActive
            ? "Apagar os comandos guardados desta sessão? Isso não pode ser desfeito."
            : "Apagar todo o histórico de comandos de todas as sessões? Isso não pode ser desfeito.";
        if (await Prompts.ConfirmAsync("Limpar histórico", text, "Apagar", danger: true))
        {
            _history.Clear(onlyActive ? _activeSession : null);
            Refresh();
        }
    }
}
