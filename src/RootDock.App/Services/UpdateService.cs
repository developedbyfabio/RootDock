using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace RootDock.App.Services;

public enum UpdateStage
{
    /// <summary>Cópia que não veio do instalador (ex.: executada pelo Visual Studio): sem atualização automática.</summary>
    Unavailable,
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Baixada e pronta: instala ao reiniciar ou ao fechar o RootDock.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Atualização automática pelas Releases do GitHub (Velopack). Procura uma versão nova ao abrir e a
/// cada poucas horas, baixa em segundo plano (só a diferença, quando possível) e instala ao fechar
/// o RootDock, ou na hora com "Reiniciar e atualizar". Quem publica uma versão é o workflow
/// .github/workflows/release.yml, disparado por uma tag vX.Y.Z.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/developedbyfabio/RootDock";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    // Logo depois de abrir (a consulta e o download rodam fora da thread da interface): a versão
    // nova costuma estar baixada em poucos segundos, e o RootDock pergunta se atualiza.
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly Dispatcher _dispatcher;
    private readonly string[] _restartArgs;
    private readonly DispatcherTimer _timer;
    private VelopackAsset? _ready;
    private bool _restartRequested;
    private bool _busy;

    public UpdateService(Dispatcher dispatcher, string[] restartArgs)
    {
        _dispatcher = dispatcher;
        _restartArgs = restartArgs;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync();
        };

        try
        {
            _manager = new UpdateManager(CreateSource(), new UpdateOptions(), null);
            if (!_manager.IsInstalled)
            {
                _manager = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Atualizador indisponível", ex);
            _manager = null;
        }

        CurrentVersion = _manager?.CurrentVersion?.ToString() ?? AppInfo.Version;
        Stage = _manager is null ? UpdateStage.Unavailable : UpdateStage.Idle;

        // Atualização baixada numa execução anterior e ainda não instalada.
        if (_manager?.UpdatePendingRestart is { } pending && IsNewer(pending))
        {
            _ready = pending;
            Show(pending);
            Stage = UpdateStage.Ready;
        }
    }

    /// <summary>Versão para a qual o RootDock acabou de ser atualizado (aviso de "atualizado" na abertura).</summary>
    public static string? RestartedVersion { get; set; }

    public static UpdateService? Current { get; set; }

    public bool IsSupported => _manager is not null;

    public bool IsPortable => _manager?.IsPortable == true;

    public UpdateStage Stage { get; private set; }

    public string CurrentVersion { get; }

    /// <summary>Versão nova encontrada (baixando ou pronta).</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Novidades da versão nova, em Markdown (vêm da tag ou dos commits).</summary>
    public string? ReleaseNotes { get; private set; }

    public int Progress { get; private set; }

    public string? Error { get; private set; }

    public DateTime? LastCheck { get; private set; }

    /// <summary>Procurar e baixar sozinho (Ajuda → Atualizações). Desligado, só verifica quando pedido.</summary>
    public bool Automatic { get; private set; }

    /// <summary>Mudou o estágio, o progresso ou a versão encontrada (sempre na thread da interface).</summary>
    public event EventHandler? Changed;

    /// <summary>Uma versão nova terminou de baixar.</summary>
    public event EventHandler? Ready;

    public void SetAutomatic(bool enabled)
    {
        Automatic = enabled;
        _timer.Stop();
        if (enabled && IsSupported)
        {
            _timer.Interval = LastCheck is null ? FirstCheckDelay : CheckInterval;
            _timer.Start();
        }
    }

    /// <summary>Procura uma versão nova e, se houver, baixa em segundo plano. Nunca lança.</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || _busy)
        {
            return;
        }

        _busy = true;
        Error = null;
        SetStage(UpdateStage.Checking);
        try
        {
            var manager = _manager;
            var info = await Task.Run(manager.CheckForUpdatesAsync);
            LastCheck = DateTime.Now;
            if (info is null || !IsNewer(info.TargetFullRelease))
            {
                SetStage(_ready is null ? UpdateStage.UpToDate : UpdateStage.Ready);
                return;
            }

            Show(info.TargetFullRelease);
            Progress = 0;
            SetStage(UpdateStage.Downloading);
            await Task.Run(() => manager.DownloadUpdatesAsync(info, p => _dispatcher.BeginInvoke(() =>
            {
                Progress = Math.Clamp(p, 0, 100);
                Changed?.Invoke(this, EventArgs.Empty);
            }), CancellationToken.None));

            _ready = info.TargetFullRelease;
            Log.Info($"Atualização {AvailableVersion} baixada.");
            SetStage(UpdateStage.Ready);
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Verificação de atualização", ex);
            Error = Describe(ex);
            if (_ready is not null)
            {
                Show(_ready); // a versão já baixada continua valendo
            }

            SetStage(_ready is null ? UpdateStage.Failed : UpdateStage.Ready);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Pede para reiniciar já com a versão nova (a instalação acontece quando o RootDock fechar).</summary>
    public void RequestRestart() => _restartRequested = _ready is not null;

    /// <summary>O usuário desistiu de fechar: a versão baixada volta a ser instalada só ao fechar.</summary>
    public void CancelRestart() => _restartRequested = false;

    public bool RestartRequested => _restartRequested;

    /// <summary>
    /// Chamado ao sair: se há versão baixada, o atualizador espera o RootDock fechar, instala e
    /// (se pedido) abre de novo com os mesmos argumentos. Fechar normalmente também instala, em silêncio.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null)
        {
            return;
        }

        try
        {
            Log.Info($"Instalando a atualização {_ready.Version} ao sair (reiniciar: {_restartRequested}).");
            _manager.WaitExitThenApplyUpdates(_ready, silent: !_restartRequested, restart: _restartRequested, _restartArgs);
        }
        catch (Exception ex)
        {
            // Sem problema: o Velopack também instala a versão baixada na próxima abertura.
            Log.Error("Aplicar atualização ao sair", ex);
        }
    }

    public static void OpenReleasesPage() => OpenUrl(ReleasesUrl);

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Abrir " + url, ex);
        }
    }

    private void Show(VelopackAsset asset)
    {
        AvailableVersion = asset.Version.ToString();
        ReleaseNotes = string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : asset.NotesMarkdown.Trim();
    }

    private bool IsNewer(VelopackAsset asset) => _manager?.CurrentVersion is not { } current || asset.Version > current;

    private void SetStage(UpdateStage stage)
    {
        Stage = stage;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Releases do GitHub. ROOTDOCK_UPDATE_URL troca a origem (pasta local ou endereço http com
    /// releases.win.json): serve para testar uma versão antes de publicar ou para um espelho interno.
    /// </summary>
    private static IUpdateSource CreateSource()
    {
        var custom = Environment.GetEnvironmentVariable("ROOTDOCK_UPDATE_URL");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return Directory.Exists(custom) ? new SimpleFileSource(new DirectoryInfo(custom)) : new SimpleWebSource(custom);
        }

        return new GithubSource(RepositoryUrl, null, false, null);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "O GitHub limitou as consultas desta rede por enquanto. A próxima verificação automática tenta de novo.",
        HttpRequestException or TaskCanceledException =>
            "Sem conexão com o GitHub. Confira a internet ou o proxy da rede.",
        IOException => "Não foi possível gravar a atualização: " + ex.Message,
        _ => ex.Message,
    };
}

/// <summary>Versão e autoria do RootDock em execução (a versão do pacote instalado coincide com esta nas versões publicadas).</summary>
public static class AppInfo
{
    public const string Author = "Fabio Henrique de Almeida Lemes";

    public static string Version { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppInfo).Assembly;
        var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        return info?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "?";
    }
}

/// <summary>Registro do Velopack no log do RootDock (só avisos e erros).</summary>
internal sealed class UpdateLog : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        if (logLevel < VelopackLogLevel.Warning)
        {
            return;
        }

        if (exception is null)
        {
            Services.Log.Info($"[atualizador] {message}");
        }
        else
        {
            Services.Log.Error($"[atualizador] {message}", exception);
        }
    }
}
