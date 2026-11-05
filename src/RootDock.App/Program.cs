using RootDock.App.Services;
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
        try
        {
            VelopackApp.Build()
                .SetLogger(new UpdateLog())
                .OnRestarted(version => UpdateService.RestartedVersion = version.ToString())
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
}
