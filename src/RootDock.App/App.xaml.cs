using System.IO;
using System.Windows;
using System.Windows.Threading;
using RootDock.App.Services;
using RootDock.Core;

namespace RootDock.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Exceção não tratada", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Tarefa com exceção não observada", args.Exception);
            args.SetObserved();
        };

        // Fundo de todas as janelas com a cor do tema (o WPF-UI pintaria o cinza padrão dele por
        // baixo do conteúdo dos diálogos). Vale também para as janelas abertas depois.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { Content: System.Windows.Controls.Panel { Background: null } root } window)
            {
                root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, window is MainWindow ? "RdBackground" : "RdSurface");
            }
        }));

        // RootDock.exe --data "D:\RootDock"  (modo portátil ou testes, sem tocar em %APPDATA%)
        var dataOption = ReadOption(e.Args, "--data");
        var paths = dataOption is { Length: > 0 } dataDir ? new AppPaths(Path.GetFullPath(dataDir)) : AppPaths.Default;
        paths.EnsureCreated();

        // Ao reiniciar depois de atualizar, volta com a mesma pasta de dados (o resto não se repete).
        UpdateService.Current = new UpdateService(Dispatcher, dataOption is { Length: > 0 } ? ["--data", Path.GetFullPath(dataOption)] : []);
        var window = new MainWindow(paths)
        {
            // RootDock.exe --open "Nome da sessão"  (útil para atalhos na área de trabalho)
            StartupSessionName = ReadOption(e.Args, "--open"),
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Versão nova já baixada: instala agora que o RootDock está fechando.
        UpdateService.Current?.ApplyOnExit();
        base.OnExit(e);
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{e.Exception.Message}\n\nDetalhes em {AppPaths.Default.LogsDirectory}",
            "RootDock",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
