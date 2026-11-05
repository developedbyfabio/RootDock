using System.Windows;
using System.Windows.Controls;
using RootDock.App.Rdp;
using RootDock.App.Services;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Usuário e senha da Área de Trabalho Remota (com os logins salvos da sessão).</summary>
public partial class RdpLoginWindow : FluentWindow
{
    private readonly IReadOnlyList<SavedLogin> _saved;
    private RdpCredentials? _result;

    private RdpLoginWindow(SessionEntry entry, IReadOnlyList<SavedLogin> saved, string? message)
    {
        InitializeComponent();
        if (entry.Kind == SessionKind.Ftp)
        {
            Title = TitleBar.Title = entry.Ftp?.Security == Core.Ftp.FtpSecurity.None ? "FTP" : "FTPS";
            UserHint.Text = "Usuário da conta FTP (vazio = anônimo).";
            UserBox.ToolTip = "Usuário da conta FTP";
            KindIcon.Symbol = SymbolRegular.FolderArrowUp20;
        }

        DialogCentering.KeepCentered(this);
        _saved = saved;
        HeaderText.Text = entry.Name;
        HostText.Text = entry.Port == SessionEntry.DefaultPort(entry.Kind) ? entry.Host : $"{entry.Host}:{entry.Port}";
        if (entry.Environment == SessionEnvironment.Production)
        {
            EnvBar.Background = (System.Windows.Media.Brush)FindResource("RdProd");
            HostText.Text += "  ·  PRODUÇÃO";
        }

        UserBox.ItemsSource = saved.Select(s => s.Username).ToList();
        UserBox.Text = entry.Username ?? saved.FirstOrDefault()?.Username ?? "";
        RememberBox.IsChecked = true;
        if (message is not null)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        UpdateSavedHint();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UserBox.Text))
            {
                UserBox.Focus();
            }
            else
            {
                PasswordBox.Focus();
            }
        };
    }

    /// <summary>Pergunta o login. Retorna null se cancelado.</summary>
    public static RdpCredentials? Ask(Window owner, SessionEntry entry, IReadOnlyList<SavedLogin> saved, Func<string, string?> savedPassword, string? message = null)
    {
        var dialog = new RdpLoginWindow(entry, saved, message) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog._result is not { } result)
        {
            return null;
        }

        // Em branco com senha salva: usa a do cofre.
        return result.Password is null ? result with { Password = savedPassword(result.Username), Remember = false } : result;
    }

    private void OnUserChanged(object sender, SelectionChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateSavedHint);

    private bool HasSavedPassword(string user) => _saved.Any(s => s.Username == user && s.HasPassword);

    private void UpdateSavedHint()
    {
        var saved = HasSavedPassword(UserBox.Text.Trim());
        SavedHint.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.PlaceholderText = saved ? "•••••••• (salva)" : "Senha";
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0 && Title is "FTP" or "FTPS")
        {
            user = "anonymous";
        }

        if (user.Length == 0)
        {
            ErrorText.Text = "Informe o usuário.";
            ErrorText.Visibility = Visibility.Visible;
            UserBox.Focus();
            return;
        }

        var typed = PasswordBox.Password;
        _result = typed.Length == 0 && HasSavedPassword(user)
            ? new RdpCredentials(user, null, false)
            : new RdpCredentials(user, typed, RememberBox.IsChecked == true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
