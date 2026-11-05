using System.Diagnostics;
using System.IO;
using System.Windows;
using RootDock.App.Services;
using RootDock.Core.Security;
using RootDock.Core.Ssh;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Mostra a chave SSH deste computador ou cria uma (Ed25519 em ~/.ssh).</summary>
public partial class SshKeyWindow : FluentWindow
{
    private SshKeyInfo? _key;

    private SshKeyWindow()
    {
        InitializeComponent();
        DialogCentering.KeepCentered(this);
        Display(SshKeys.FindDefault());
        ShowAgent();
    }

    /// <summary>Chaves carregadas no ssh-agent do Windows ou no Pageant (usadas com "Usar as chaves do agente").</summary>
    private void ShowAgent()
    {
        try
        {
            if (SshAgentClient.Find() is not { } agent)
            {
                AgentText.Text = "Nenhum agente aberto. Para usar chaves sem arquivo (ou com senha digitada uma vez por dia), " +
                                 "ligue o serviço \"OpenSSH Authentication Agent\" do Windows e use ssh-add, ou abra o Pageant do PuTTY. " +
                                 "Depois marque \"Usar as chaves do agente\" na sessão.";
                return;
            }

            var keys = agent.ListKeys();
            AgentText.Text = keys.Count == 0
                ? $"{agent.Name} aberto, mas sem chaves carregadas (use ssh-add ou adicione no Pageant)."
                : $"{agent.Name}: {keys.Count} {(keys.Count == 1 ? "chave carregada" : "chaves carregadas")}. " +
                  "Sessões com \"Usar as chaves do agente\" tentam estas chaves antes da senha.";
            foreach (var key in keys)
            {
                AgentKeys.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = $"•  {key.Type.Replace("ssh-", "")}  {key.Comment}   {key.Fingerprint}",
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
        {
            AgentText.Text = "Não foi possível consultar o agente: " + ex.Message;
        }
    }

    public static void ShowFor(Window owner) => new SshKeyWindow { Owner = owner }.ShowDialog();

    private void Display(SshKeyInfo? key)
    {
        _key = key;
        var hasKey = key is not null;
        KeyPanel.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        if (key is null)
        {
            HeaderText.Text = "Você ainda não tem uma chave SSH";
            IntroText.Text =
                "Uma chave SSH substitui a senha: o servidor guarda a parte pública e só este computador tem a parte privada.\n\n" +
                $"Ela será criada em {Path.Combine(SshKeys.DefaultDirectory, "id_ed25519")} (Ed25519, o padrão atual do OpenSSH; " +
                "também serve para o ssh do Windows, Git e VS Code). O arquivo fica acessível só para o seu usuário.";
            return;
        }

        HeaderText.Text = "Sua chave SSH";
        IntroText.Text = "Esta é a chave que o RootDock instala nos servidores. Ela já existia neste computador ou foi criada pelo RootDock.";
        TypeText.Text = key.DisplayType + (key.Comment.Length > 0 ? $"  ·  {key.Comment}" : "");
        PathText.Text = key.PrivateKeyPath;
        PathText.ToolTip = key.PrivateKeyPath;
        FingerprintText.Text = key.Fingerprint;
        // Só para exibir: espaços inquebráveis fazem a linha quebrar por caractere, sem buracos.
        // A cópia usa o texto original (botão Copiar).
        PublicKeyText.Text = key.PublicKey.Replace(' ', '\u00A0');
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            Display(SshKeys.GenerateEd25519(SshKeys.DefaultComment()));
            HeaderText.Text = "Chave criada";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Criar chave SSH", ex);
            ErrorText.Text = "Não foi possível criar a chave: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_key is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_key.PublicKey);
            ((Wpf.Ui.Controls.Button)sender).Content = "Copiada";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ErrorText.Text = "A área de transferência está ocupada por outro programa; tente de novo.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_key is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_key.PrivateKeyPath}\"") { UseShellExecute = true });
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
