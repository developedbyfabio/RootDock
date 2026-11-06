using System.Text.Json;
using RootDock.Core.Sessions;

namespace RootDock.Core.Settings;

public sealed class AppSettings
{
    public int FontSize { get; set; } = 14;

    /// <summary>Tema da interface (Exibir → Tema): escuro, claro, grafite, meia-noite, matrix, ambar, fosforo.</summary>
    public string AppTheme { get; set; } = "escuro";

    /// <summary>Procurar, baixar e instalar versões novas sozinho (Ajuda → Atualizações).</summary>
    public bool AutoUpdate { get; set; } = true;

    public LocalShellKind DefaultLocalShell { get; set; } = LocalShellKind.PowerShell;

    public double SidebarWidth { get; set; } = 280;

    /// <summary>Como a lista de sessões é agrupada: Folders, Company ou Environment.</summary>
    public string TreeGrouping { get; set; } = "Folders";

    public WindowPlacement? Window { get; set; }

    public List<string> CollapsedFolders { get; set; } = [];

    public bool CommandsPanelOpen { get; set; }

    public double CommandsPanelWidth { get; set; } = 360;

    /// <summary>Ids dos comandos marcados com estrela.</summary>
    public List<string> FavoriteCommands { get; set; } = [];

    /// <summary>Ids dos comandos usados por último (mais recente primeiro).</summary>
    public List<string> RecentCommands { get; set; } = [];

    /// <summary>Comandos cadastrados pelo usuário ("Meus comandos").</summary>
    public List<CustomCommand> CustomCommands { get; set; } = [];

    /// <summary>Sistema dos comandos do painel: "auto" (segue o terminal em foco) ou uma família fixa (debian, rhel, suse, windows).</summary>
    public string CommandsOs { get; set; } = "auto";

    /// <summary>Colunas do painel de arquivos: larguras, escondidas e ordem.</summary>
    public Sftp.FileColumnSettings FilesColumns { get; set; } = new();

    /// <summary>Macros (sequências de comandos, pausas e esperas) criadas ou gravadas pelo usuário.</summary>
    public List<Macros.CommandMacro> Macros { get; set; } = [];

    /// <summary>Últimos comandos executados em vários servidores (mais recente primeiro).</summary>
    public List<string> MultiExecHistory { get; set; } = [];

    /// <summary>Opções da execução em vários servidores.</summary>
    public int MultiExecConcurrency { get; set; } = 5;

    public int MultiExecTimeoutSeconds { get; set; } = 120;

    /// <summary>Aparência e comportamento do terminal.</summary>
    public TerminalAppearance Terminal { get; set; } = new();

    /// <summary>Abas abertas quando o RootDock foi fechado (para reabrir com um clique).</summary>
    public List<WorkspaceTab> LastWorkspace { get; set; } = [];
}

/// <summary>Uma aba da última sessão de trabalho: sessão salva ou terminal local.</summary>
public sealed class WorkspaceTab
{
    public Guid? SessionId { get; set; }

    public RootDock.Core.Sessions.LocalShellKind? LocalShell { get; set; }
}

/// <summary>Preferências do terminal (fonte, cursor, cores e comportamento do mouse).</summary>
public sealed class TerminalAppearance
{
    public string FontFamily { get; set; } = "Cascadia Mono";

    public double LineHeight { get; set; } = 1.1;

    /// <summary>"bar", "block" ou "underline".</summary>
    public string CursorStyle { get; set; } = "bar";

    public bool CursorBlink { get; set; } = true;

    public int Scrollback { get; set; } = 20000;

    /// <summary>Id do tema de cores (ver TerminalThemes no app).</summary>
    public string Theme { get; set; } = "noite";

    /// <summary>Destacar ERROR, WARNING, FAILED... na saída.</summary>
    public bool Highlight { get; set; } = true;

    public bool CopyOnSelect { get; set; } = true;

    /// <summary>Botão direito cola (como no PuTTY/MobaXterm); desligado, abre um menu.</summary>
    public bool RightClickPaste { get; set; } = true;

    /// <summary>Animação por cima do terminal parado (o texto continua visível atrás).</summary>
    public bool Screensaver { get; set; } = true;

    /// <summary>Minutos sem tecla nem mouse num terminal até a animação aparecer nele.</summary>
    public int ScreensaverMinutes { get; set; } = 10;

    /// <summary>Cena da animação: "random" (uma diferente a cada vez) ou o id de uma cena (turma, chuva, tv).
    /// Id desconhecido (cena que saiu do app) vira "random" na hora de mostrar.</summary>
    public string ScreensaverScene { get; set; } = "random";
}

public sealed class CustomCommand
{
    public string Id { get; set; } = "meu." + Guid.NewGuid().ToString("N")[..8];

    public string Title { get; set; } = "";

    /// <summary>Pode ter campos {{nome}}, preenchidos na hora de executar.</summary>
    public string Cmd { get; set; } = "";

    public string? Description { get; set; }

    public bool Caution { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>Preferências do usuário. Falha de leitura volta ao padrão sem apagar o arquivo.</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    private static readonly object Gate = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return Read(_path);
            }
        }
        catch (JsonException)
        {
            // Arquivo inválido (ex.: desligou no meio da gravação): guarda para análise e usa o backup.
            // Sem isso, o próximo Save apagaria "Meus comandos", favoritos etc. de vez.
            TryMove(_path, _path + ".bad");
            try
            {
                if (File.Exists(_path + ".bak"))
                {
                    return Read(_path + ".bak");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
            }
        }
        catch (IOException)
        {
        }

        return new AppSettings();
    }

    /// <summary>Gravação atômica: temporário com flush no disco, depois troca mantendo a versão anterior em .bak.</summary>
    public void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, SessionStore.JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }

    private static AppSettings Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppSettings>(stream, SessionStore.JsonOptions) ?? new AppSettings();
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
