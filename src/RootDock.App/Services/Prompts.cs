using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RootDock.App.Services;

/// <summary>Diálogos curtos no tema do aplicativo.</summary>
public static class Prompts
{
    public static async Task<bool> ConfirmAsync(string title, string text, string primary = "Confirmar", bool danger = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancelar",
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public static async Task<string?> AskTextAsync(string title, string placeholder, string initial = "", string primary = "OK")
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = initial, MinWidth = 340 };
        input.Loaded += (_, _) =>
        {
            input.Focus();
            // Renomear: seleciona o nome sem a extensão.
            var dot = initial.LastIndexOf('.');
            if (dot > 0)
            {
                input.Select(0, dot);
            }
            else
            {
                input.SelectAll();
            }
        };
        var box = new TextPromptBox
        {
            Title = title,
            Content = input,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancelar",
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                box.Accept();
            }
        };
        var result = await box.ShowDialogAsync();
        var accepted = result == MessageBoxResult.Primary;
        var text = input.Text?.Trim();
        return accepted && !string.IsNullOrEmpty(text) ? text : null;
    }

    public static Task InfoAsync(string title, string text) =>
        new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "OK",
        }.ShowDialogAsync();
}

/// <summary>MessageBox que aceita com Enter no campo de texto.</summary>
internal sealed class TextPromptBox : MessageBox
{
    public void Accept() => OnButtonClick(Wpf.Ui.Controls.MessageBoxButton.Primary);
}
