using System.Windows;
using RootDock.App.Services;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>
/// Pergunta ao abrir o RootDock quando uma versão nova acabou de baixar: "Atualizar agora" reinicia
/// já atualizado; "Depois" deixa para instalar ao fechar (o aviso da barra de título continua).
/// </summary>
public partial class UpdatePromptWindow : FluentWindow
{
    private bool _update;

    private UpdatePromptWindow(UpdateService updates, int openConnections)
    {
        InitializeComponent();
        TitleText.Text = $"Versão {updates.AvailableVersion} disponível";
        DetailText.Text = $"Já foi baixada e está pronta para instalar. Você está usando a {updates.CurrentVersion}.";
        if (updates.ReleaseNotes is { Length: > 0 } notes)
        {
            NotesText.Text = UpdateWindow.PlainNotes(notes);
            NotesPanel.Visibility = Visibility.Visible;
        }

        HintText.Text = openConnections > 0
            ? $"Atualizar agora fecha {Plural(openConnections)} (dá para reabrir com um clique depois). Se escolher Depois, a versão nova é instalada quando você fechar o RootDock."
            : "O RootDock fecha e abre de novo em poucos segundos. Se escolher Depois, a versão nova é instalada quando você fechar o programa.";
        Loaded += (_, _) => LaterButton.Focus();
    }

    /// <summary>Mostra a pergunta; true = atualizar agora.</summary>
    public static bool Ask(Window owner, UpdateService updates, int openConnections)
    {
        var window = new UpdatePromptWindow(updates, openConnections) { Owner = owner };
        window.ShowDialog();
        return window._update;
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        _update = true;
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private static string Plural(int n) => n == 1 ? "a aba aberta" : $"as {n} abas abertas";
}
