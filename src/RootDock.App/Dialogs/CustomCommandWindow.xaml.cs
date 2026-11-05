using System.Windows;
using System.Windows.Controls;
using RootDock.Core.Commands;
using RootDock.Core.Settings;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

public partial class CustomCommandWindow : FluentWindow
{
    private readonly CustomCommand _command;

    private CustomCommandWindow(CustomCommand command, bool isNew)
    {
        InitializeComponent();
        _command = command;
        TitleBar.Title = isNew ? "Novo comando" : "Editar comando";
        TitleBox.Text = command.Title;
        CommandBox.Text = command.Cmd;
        DescriptionBox.Text = command.Description ?? "";
        CautionCheck.IsChecked = command.Caution;
        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>Cria ou edita um comando do usuário. Retorna null se cancelado.</summary>
    public static CustomCommand? Edit(Window owner, CustomCommand? existing)
    {
        var copy = existing is null
            ? new CustomCommand()
            : new CustomCommand { Id = existing.Id, Title = existing.Title, Cmd = existing.Cmd, Description = existing.Description, Caution = existing.Caution };
        var dialog = new CustomCommandWindow(copy, existing is null) { Owner = owner };
        return dialog.ShowDialog() == true ? copy : null;
    }

    private void OnCommandChanged(object sender, TextChangedEventArgs e)
    {
        var dangerous = CommandSafety.LooksDestructive(CommandBox.Text ?? "");
        DangerText.Visibility = dangerous ? Visibility.Visible : Visibility.Collapsed;
        if (dangerous)
        {
            CautionCheck.IsChecked = true;
        }

        CautionCheck.IsEnabled = !dangerous;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim() ?? "";
        var command = CommandBox.Text?.Trim() ?? "";
        if (title.Length == 0 || command.Length == 0)
        {
            ShowError("Preencha o nome e o comando.");
            return;
        }

        if (command.Contains('\n') || command.Contains('\r'))
        {
            ShowError("Use uma única linha (encadeie com && se precisar de mais de um comando).");
            return;
        }

        _command.Title = title;
        _command.Cmd = command;
        _command.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        _command.Caution = CautionCheck.IsChecked == true || CommandSafety.LooksDestructive(command);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
