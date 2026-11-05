using System.IO;
using RootDock.App.Services;
using RootDock.Core;
using Velopack;

namespace RootDock.App;

/// <summary>
/// Ponto de entrada. O Velopack precisa rodar antes de qualquer coisa: é ele que trata os
/// comandos do instalador/desinstalador (e sai em seguida) e aplica uma atualização já baixada.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        StartupTrace.Begin();
        try
        {
            VelopackApp.Build()
                .SetLogger(new UpdateLog())
                .OnRestarted(version => UpdateService.RestartedVersion = version.ToString())
                .OnBeforeUninstallFastCallback(_ => RemoveCache())
                .Run();
        }
        catch (Exception ex)
        {
            // Falha no atualizador não pode impedir o RootDock de abrir.
            Log.Error("Inicialização do atualizador", ex);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Ao desinstalar: apaga o cache da tela (WebView2), que fica fora da pasta do programa. Sessões,
    /// preferências (%APPDATA%\RootDock) e senhas (Cofre do Windows) ficam, para uma reinstalação.
    /// </summary>
    private static void RemoveCache()
    {
        try
        {
            if (Directory.Exists(AppPaths.DefaultWebViewDirectory))
            {
                Directory.Delete(AppPaths.DefaultWebViewDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Arquivo preso (outra cópia aberta): sobra só cache, sem dado do usuário.
        }
    }
}
