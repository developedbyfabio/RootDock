using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RootDock.App.Services;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>
/// Gerencia os logins de uma sessão: adicionar, trocar senha, definir o padrão, remover,
/// perguntar ao abrir e parar de usar a chave SSH. Cada ação é gravada na hora.
/// </summary>
public partial class LoginsWindow : FluentWindow
{
    private readonly SessionEntry _entry;
    private readonly SessionManager _sessions;
    private readonly SessionLogins _logins;
    private string? _confirmRemove;

    private LoginsWindow(SessionEntry entry, SessionManager sessions, SessionLogins logins)
    {
        InitializeComponent();
        DialogCentering.KeepCentered(this);
        _entry = entry;
        _sessions = sessions;
        _logins = logins;
        HeaderText.Text = entry.Name;
        EndpointText.Text = entry.Port == 22 ? entry.Host : $"{entry.Host}:{entry.Port}";
        ChooseBox.IsChecked = entry.ChooseLoginOnConnect;
        Refresh();
        Loaded += (_, _) => UserBox.Focus();
    }

    /// <summary>Abre para a sessão salva (a instância do documento, para gravar direto).</summary>
    public static void ShowFor(Window owner, SessionEntry entry, SessionManager sessions, SessionLogins logins) =>
        new LoginsWindow(entry, sessions, logins) { Owner = owner }.ShowDialog();

    private void Refresh()
    {
        Rows.Children.Clear();
        IReadOnlyList<SavedLogin> list;
        try
        {
            list = _logins.List(_entry);
        }
        catch (Win32Exception ex)
        {
            Log.Error("Listar logins", ex);
            list = [];
        }

        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < list.Count; i++)
        {
            Rows.Children.Add(BuildRow(list[i], first: i == 0));
        }

        var key = _entry.PrivateKeyPath;
        KeyText.Text = key is null
            ? "Sem chave SSH. Para entrar sem senha, use \"Instalar minha chave SSH\" no botão direito da sessão."
            : $"Entra com a chave SSH {System.IO.Path.GetFileName(key)} ({key}).";
        RemoveKeyButton.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border BuildRow(SavedLogin login, bool first)
    {
        var row = new DockPanel { Margin = new Thickness(14, 8, 8, 8), LastChildFill = true };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (!login.IsDefault)
        {
            actions.Children.Add(SmallButton("Tornar padrão", () => Apply(() => _logins.SetDefault(_entry, login.Username))));
        }

        actions.Children.Add(SmallButton(login.HasPassword ? "Trocar senha" : "Salvar senha", () => EditPassword(login.Username)));
        var confirming = _confirmRemove == login.Username;
        var remove = SmallButton(confirming ? "Confirmar remoção" : "Remover", () =>
        {
            if (_confirmRemove != login.Username)
            {
                _confirmRemove = login.Username; // primeiro clique só pede confirmação
                Refresh();
                return;
            }

            _confirmRemove = null;
            Apply(() => _logins.Remove(_entry, login.Username));
        });
        if (confirming)
        {
            remove.Appearance = ControlAppearance.Danger;
        }

        actions.Children.Add(remove);
        row.Children.Add(actions);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = login.Username, FontWeight = FontWeights.SemiBold };
        if (login.IsDefault)
        {
            name.Inlines.Add(new System.Windows.Documents.Run("   padrão")
            {
                FontWeight = FontWeights.Normal,
                FontSize = 11,
                Foreground = (Brush)FindResource("RdAccent"),
            });
        }

        info.Children.Add(name);
        info.Children.Add(new TextBlock
        {
            Text = login.HasPassword ? "Senha salva no Cofre do Windows" : "Sem senha: pede ao conectar",
            FontSize = 11,
            Foreground = (Brush)FindResource("RdTextDim"),
        });
        row.Children.Add(info);

        return new Border
        {
            Child = row,
            BorderBrush = (Brush)FindResource("RdBorder"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };
    }

    private static Button SmallButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        button.Click += (_, _) => click();
        return button;
    }

    private void EditPassword(string username)
    {
        UserBox.Text = username;
        FormTitle.Text = $"Trocar a senha de {username}";
        PasswordBox.Password = "";
        PasswordBox.Focus();
    }

    private void OnSaveLogin(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0 || user.Contains(' '))
        {
            FormError.Text = "Informe o usuário (sem espaços).";
            FormError.Visibility = Visibility.Visible;
            UserBox.Focus();
            return;
        }

        var password = PasswordBox.Password;
        Apply(() => _logins.Save(_entry, user, password.Length == 0 ? null : password));
        UserBox.Text = "";
        PasswordBox.Password = "";
        FormTitle.Text = "Adicionar login";
        FormError.Visibility = Visibility.Collapsed;
        UserBox.Focus();
    }

    private void OnChooseChanged(object sender, RoutedEventArgs e) =>
        Apply(() => _entry.ChooseLoginOnConnect = ChooseBox.IsChecked == true);

    private void OnRemoveKey(object sender, RoutedEventArgs e) =>
        Apply(() => _entry.PrivateKeyPath = null); // o arquivo da chave continua no computador

    private void Apply(Action change)
    {
        try
        {
            change();
            _sessions.Save();
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            Log.Error("Logins salvos", ex);
            FormError.Text = ex.Message;
            FormError.Visibility = Visibility.Visible;
        }

        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
