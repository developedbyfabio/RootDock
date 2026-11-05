using System.Windows;
using RootDock.App.Dialogs;
using RootDock.App.Services;

namespace RootDock.App;

/// <summary>
/// Atualização automática na janela principal: aviso "Reiniciar para atualizar" na barra de título
/// quando uma versão nova termina de baixar, Ajuda → Verificar atualizações e o reinício com confirmação.
/// </summary>
public partial class MainWindow
{
    private UpdateService? Updates => UpdateService.Current;

    private void InitUpdates()
    {
        if (Updates is not { } updates)
        {
            return;
        }

        updates.SetAutomatic(_settings.AutoUpdate);
        updates.Changed += (_, _) => RefreshUpdatePill();
        updates.Ready += (_, _) =>
            Notify($"A versão {updates.AvailableVersion} do RootDock foi baixada. Ela é instalada ao fechar, ou clique em \"Reiniciar para atualizar\".", false);
        RefreshUpdatePill();
    }

    /// <summary>Aviso na abertura logo depois de uma atualização.</summary>
    private void AnnounceUpdate()
    {
        if (UpdateService.RestartedVersion is { } version)
        {
            UpdateService.RestartedVersion = null;
            Log.Info($"Reaberto depois de atualizar para {version}.");
            Notify($"RootDock atualizado para a versão {version}.", false);
        }
    }

    private void RefreshUpdatePill()
    {
        var ready = Updates is { Stage: UpdateStage.Ready };
        UpdatePill.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready)
        {
            UpdatePill.ToolTip = $"A versão {Updates!.AvailableVersion} foi baixada. Clique para ver as novidades e reiniciar.";
        }
    }

    private void OnUpdatePill(object sender, RoutedEventArgs e) => OpenUpdates(checkNow: false);

    private void OpenUpdates(bool checkNow)
    {
        if (Updates is not { } updates)
        {
            return;
        }

        UpdateWindow.ShowFor(this, updates, enabled =>
        {
            _settings.AutoUpdate = enabled;
            SaveSettings();
        }, RestartForUpdate, checkNow);
    }

    /// <summary>Fecha o RootDock (com a confirmação de sempre se houver conexões) e abre de novo já atualizado.</summary>
    private void RestartForUpdate()
    {
        Updates?.RequestRestart();
        Close();
    }
}
