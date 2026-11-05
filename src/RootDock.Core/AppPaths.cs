namespace RootDock.Core;

/// <summary>Locais de dados do RootDock no perfil do usuário.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>%APPDATA%\RootDock</summary>
    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RootDock"));

    public string DataDirectory { get; }

    public string SessionsFile => Path.Combine(DataDirectory, "sessions.json");

    public string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Cache do WebView2. Fica fora do %APPDATA% (que pode ser sincronizado) no perfil padrão; com
    /// --data, vai junto da pasta de dados para cada instância portátil ter o seu navegador.
    /// </summary>
    public string WebViewDirectory => ReferenceEquals(this, Default)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RootDock", "WebView2")
        : Path.Combine(DataDirectory, "WebView2");

    public void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
