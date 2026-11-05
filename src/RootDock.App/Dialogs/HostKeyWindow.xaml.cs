using System.Windows;
using RootDock.Core.Security;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

public enum HostKeyDecision
{
    Reject,
    TrustOnce,
    TrustAndSave,
}

public partial class HostKeyWindow : FluentWindow
{
    private HostKeyDecision _decision = HostKeyDecision.Reject;

    private HostKeyWindow()
    {
        InitializeComponent();
    }

    public static HostKeyDecision Ask(Window owner, string host, int port, HostKeyCheck check)
    {
        var dialog = new HostKeyWindow { Owner = owner };
        dialog.HostText.Text = port == 22 ? host : $"{host}:{port}";
        dialog.KeyTypeText.Text = check.KeyType;
        dialog.FingerprintText.Text = check.FingerprintSha256;

        if (check.Status == HostKeyStatus.Changed)
        {
            dialog.WarningBar.Background = (System.Windows.Media.Brush)dialog.FindResource("RdProd");
            dialog.HeaderText.Text = "ATENÇÃO: a chave deste servidor mudou";
            dialog.BodyText.Text =
                "A chave apresentada é diferente da que estava salva. Isso acontece quando o servidor é reinstalado, " +
                "mas também pode indicar alguém interceptando a conexão. Só aceite se tiver certeza de que o servidor foi trocado." +
                (check.Source is null ? "" : $"\n\nChave anterior registrada em: {check.Source}");
            dialog.TrustButton.Content = "Aceitar a nova chave";
            dialog.OnceButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.HeaderText.Text = "Primeira conexão com este servidor";
            dialog.BodyText.Text =
                "O RootDock ainda não conhece este servidor. Confira a impressão digital com o responsável pelo servidor " +
                "antes de confiar. Depois de salva, a chave é verificada automaticamente em todas as conexões.";
        }

        dialog.ShowDialog();
        return dialog._decision;
    }

    private void OnTrust(object sender, RoutedEventArgs e)
    {
        _decision = HostKeyDecision.TrustAndSave;
        DialogResult = true;
    }

    private void OnOnce(object sender, RoutedEventArgs e)
    {
        _decision = HostKeyDecision.TrustOnce;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
