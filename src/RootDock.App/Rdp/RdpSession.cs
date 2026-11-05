using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using RootDock.App.Services;
using RootDock.Core.Rdp;
using RootDock.Core.Sessions;

namespace RootDock.App.Rdp;

/// <summary>Estado mostrado na aba (a tela desenha o cartão de "conectando" / "desconectado").</summary>
public enum RdpState
{
    Waiting,
    Connecting,
    Connected,
    Closed,
}

/// <summary>Login usado na conexão (a senha só fica em memória enquanto a aba existe).</summary>
public sealed record RdpCredentials(string Username, string? Password, bool Remember);

/// <summary>
/// Uma Área de Trabalho Remota numa aba do RootDock. O controle do Windows é uma janela nativa
/// posta por cima do espaço que a aba reserva na tela (a página informa a posição e o tamanho);
/// quando a aba fica escondida ou algo da página passa por cima (menus, diálogos), ele some.
/// </summary>
public sealed class RdpSession : IDisposable
{
    private readonly Canvas _layer;
    private readonly DispatcherTimer _resizeTimer;
    private WindowsFormsHost? _host;
    private RdpClientHost? _client;
    private Rect _bounds;
    private bool _visible;
    private bool _loggedIn;
    private bool _userDisconnect;
    private bool _disposed;
    private bool _fullScreen;
    private (int Width, int Height) _desktop;

    public RdpSession(string id, SessionEntry entry, RdpCredentials credentials, Canvas layer)
    {
        Id = id;
        Entry = entry;
        Credentials = credentials;
        _layer = layer;
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            ApplyDesktopSize();
        };
    }

    public string Id { get; }

    public SessionEntry Entry { get; }

    public RdpCredentials Credentials { get; private set; }

    public RdpState State { get; private set; } = RdpState.Waiting;

    public bool IsConnected => State == RdpState.Connected;

    /// <summary>Janela nativa do controle (para o recorte na página).</summary>
    internal WindowsFormsHost? Host => _host;

    /// <summary>(estado, mensagem para o cartão da aba).</summary>
    public event Action<RdpState, string?>? StateChanged;

    /// <summary>Login aceito pelo servidor (para oferecer salvar a senha).</summary>
    public event Action? LoggedIn;

    private RdpSettings Settings => Entry.Rdp ?? new RdpSettings();

    /// <summary>Posição (em unidades WPF, relativa à página) e visibilidade do espaço da aba.</summary>
    public void SetBounds(Rect bounds, bool visible)
    {
        var resized = Math.Abs(bounds.Width - _bounds.Width) > 1 || Math.Abs(bounds.Height - _bounds.Height) > 1;
        _bounds = bounds;
        _visible = visible && bounds.Width >= 32 && bounds.Height >= 32;
        if (State == RdpState.Waiting && _visible)
        {
            Start();
            return;
        }

        Place();
        if (resized && _loggedIn && !_fullScreen && Settings.FixedSize is null)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    public void Reconnect()
    {
        if (_disposed || State is RdpState.Connecting or RdpState.Connected)
        {
            return;
        }

        DestroyClient();
        Start();
    }

    public void UpdateCredentials(RdpCredentials credentials) => Credentials = credentials;

    public void Disconnect()
    {
        if (_client is null || State != RdpState.Connected)
        {
            return;
        }

        _userDisconnect = true;
        try
        {
            _client.Call("Disconnect");
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            Log.Error("RDP desconectar", ex);
        }
    }

    public void FullScreen()
    {
        if (_client is null || State != RdpState.Connected)
        {
            return;
        }

        try
        {
            _client.Set("FullScreen", true);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            Log.Error("RDP tela cheia", ex);
        }
    }

    public void Focus()
    {
        if (_client is not null && _visible && State == RdpState.Connected)
        {
            _client.Focus();
        }
    }

    // ---------------------------------------------------------------- conexão
    private void Start()
    {
        if (_disposed)
        {
            return;
        }

        SetState(RdpState.Connecting, $"Conectando a {Entry.Host}...");
        try
        {
            CreateClient();
            Configure();
            _client!.Call("Connect");
        }
        catch (Exception ex)
        {
            Log.Error($"RDP {Entry.Host}", ex);
            DestroyClient();
            SetState(RdpState.Closed, ex is COMException { HResult: unchecked((int)0x80040154) }
                ? "O componente de Área de Trabalho Remota do Windows não está disponível neste computador."
                : $"Não foi possível iniciar a conexão: {Unwrap(ex).Message}");
        }
    }

    private void CreateClient()
    {
        var client = new RdpClientHost { Dock = System.Windows.Forms.DockStyle.Fill };
        ((System.ComponentModel.ISupportInitialize)client).BeginInit();
        var host = new WindowsFormsHost { Child = client, Visibility = Visibility.Collapsed };
        ((System.ComponentModel.ISupportInitialize)client).EndInit();
        _layer.Children.Add(host);
        host.Width = Math.Max(1, _bounds.Width);
        host.Height = Math.Max(1, _bounds.Height);
        client.CreateControl();

        client.Connecting += () => Ui(() => SetState(RdpState.Connecting, $"Conectando a {Entry.Host}..."));
        client.Connected += () => Ui(() => SetState(RdpState.Connected, null));
        client.LoginComplete += () => Ui(OnLoginComplete);
        client.Disconnected += reason => Ui(() => OnDisconnected(reason));
        client.EnteredFullScreen += () => Ui(OnEnteredFullScreen);
        client.LeftFullScreen += () => Ui(OnLeftFullScreen);
        client.MinimizeRequested += () => Ui(() =>
        {
            if (Window.GetWindow(_layer) is { } window)
            {
                window.WindowState = WindowState.Minimized;
            }
        });
        client.FatalError += code => Ui(() => SetState(RdpState.Closed, $"Erro interno do controle de Área de Trabalho Remota (código {code})."));
        _client = client;
        _host = host;
    }

    private void Configure()
    {
        var client = _client!;
        var settings = Settings;
        var (domain, user) = RdpUser.Split(Credentials.Username);
        var scale = DpiScale();
        _desktop = settings.FixedSize ?? DesktopSizeFor(_bounds, scale);

        client.Set("Server", Entry.Host);
        client.Set("UserName", user);
        if (domain is not null)
        {
            client.Set("Domain", domain);
        }

        client.Set("DesktopWidth", _desktop.Width);
        client.Set("DesktopHeight", _desktop.Height);
        client.Set("ColorDepth", 32);
        TrySet(client, "ConnectingText", $"Conectando a {Entry.Host}...");
        TrySet(client, "DisconnectedText", "Desconectado");
        TrySet(client, "FullScreenTitle", $"{Entry.Name} · RootDock");
        if (!string.IsNullOrEmpty(Credentials.Password))
        {
            client.SetPassword(Credentials.Password);
        }

        var advanced = client.Get("AdvancedSettings9");
        RdpClientHost.Set(advanced, "RDPPort", Entry.Port);
        RdpClientHost.Set(advanced, "EnableCredSspSupport", true); // NLA (exigido pela maioria dos servidores)
        RdpClientHost.Set(advanced, "AuthenticationLevel", 2);     // certificado com problema: o Windows pergunta
        RdpClientHost.Set(advanced, "RedirectClipboard", settings.RedirectClipboard);
        RdpClientHost.Set(advanced, "RedirectDrives", settings.RedirectDrives);
        RdpClientHost.Set(advanced, "RedirectPrinters", settings.RedirectPrinters);
        RdpClientHost.Set(advanced, "EnableAutoReconnect", true);
        RdpClientHost.Set(advanced, "MaxReconnectAttempts", 20);
        RdpClientHost.Set(advanced, "SmartSizing", settings.FixedSize is not null); // resolução fixa: encaixa na aba
        TrySet(advanced, "ConnectToAdministerServer", settings.AdminSession);
        TrySet(advanced, "BandwidthDetection", true);
        TrySet(advanced, "EnableSuperPan", false);

        // Tela cheia: a barra azul fica fixa no topo, com Minimizar, Restaurar (volta para a aba) e Fechar.
        TrySet(advanced, "DisplayConnectionBar", true);
        TrySet(advanced, "PinConnectionBar", true);
        TrySet(advanced, "ConnectionBarShowRestoreButton", true);
        TrySet(advanced, "ConnectionBarShowMinimizeButton", true);

        var secured = TryGet(client, "SecuredSettings3") ?? TryGet(client, "SecuredSettings2");
        TrySet(secured, "AudioRedirectionMode", settings.PlayAudio ? 0 : 2);

        // Tecla Windows, Win+R, Alt+Tab...: 1 = no servidor sempre que o controle tem o foco;
        // 2 = só em tela cheia (padrão do controle, que fazia o Win abrir o menu deste computador).
        TrySet(advanced, "EnableWindowsKey", settings.RemoteWindowsKeys ? 1 : 0);
        TrySet(secured, "KeyboardHookMode", settings.RemoteWindowsKeys ? 1 : 2);

        // Escala de DPI do servidor igual à desta tela (texto nítido em monitores de alta resolução).
        if (client.Ocx is IMsRdpExtendedSettings extended)
        {
            try
            {
                object desktopScale = (uint)DesktopScaleFactor(scale);
                object deviceScale = (uint)DeviceScaleFactor(scale);
                extended.set_Property("DesktopScaleFactor", ref desktopScale);
                extended.set_Property("DeviceScaleFactor", ref deviceScale);
            }
            catch (COMException ex)
            {
                Log.Error("RDP escala", ex);
            }
        }
    }

    private void OnLoginComplete()
    {
        _loggedIn = true;
        LoggedIn?.Invoke();
        ApplyDesktopSize(); // a aba pode ter mudado de tamanho durante o login
    }

    private void OnDisconnected(int reason)
    {
        _loggedIn = false;
        string message;
        if (_userDisconnect || reason is 1 or 2)
        {
            message = reason == 2 && !_userDisconnect ? "A sessão foi encerrada no servidor." : "Desconectado.";
        }
        else
        {
            message = Describe(reason);
        }

        _userDisconnect = false;
        if (_disposed)
        {
            return;
        }

        SetState(RdpState.Closed, message);
    }

    private string Describe(int reason)
    {
        try
        {
            var extended = Convert.ToUInt32(_client?.Get("ExtendedDisconnectReason") ?? 0);
            if (_client?.Call("GetErrorDescription", (uint)reason, extended) is string text && text.Trim().Length > 0)
            {
                return text.Trim();
            }
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException)
        {
            // segue com a mensagem genérica
        }

        return $"A conexão foi encerrada (código {reason}).";
    }

    /// <summary>Resolução automática: a área do servidor acompanha o tamanho da aba.</summary>
    private void ApplyDesktopSize()
    {
        if (_client is null || !_loggedIn || _fullScreen || Settings.FixedSize is not null || _bounds.Width < 200 || _bounds.Height < 200)
        {
            return;
        }

        var scale = DpiScale();
        ResizeSession(DesktopSizeFor(_bounds, scale), scale);
    }

    /// <summary>Tela cheia: a área de trabalho do servidor passa a ter o tamanho do monitor inteiro.</summary>
    private void OnEnteredFullScreen()
    {
        _fullScreen = true;
        _resizeTimer.Stop();
        if (_client is null || !_loggedIn || Settings.FixedSize is not null || _host is null)
        {
            return;
        }

        var monitor = System.Windows.Forms.Screen.FromHandle(_host.Handle).Bounds; // pixels reais do monitor
        var size = (Math.Clamp(monitor.Width & ~1, 200, 8192), Math.Clamp(monitor.Height, 200, 8192));
        ResizeSession(size, DpiScale());
    }

    /// <summary>Voltou da tela cheia (botão Restaurar da barra azul): volta ao tamanho da aba.</summary>
    private void OnLeftFullScreen()
    {
        _fullScreen = false;
        ApplyDesktopSize();
        Focus();
    }

    private void ResizeSession((int Width, int Height) size, double scale)
    {
        if (_client is null || size == _desktop)
        {
            return;
        }

        try
        {
            _client.Call("UpdateSessionDisplaySettings",
                (uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u,
                (uint)DesktopScaleFactor(scale), (uint)DeviceScaleFactor(scale));
            _desktop = size;
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            // Servidor antigo (sem redimensionar em tempo real): a imagem é encaixada no espaço.
            TrySet(_client.Get("AdvancedSettings9"), "SmartSizing", true);
            Log.Info($"RDP {Entry.Host}: redimensionamento dinâmico indisponível, usando ajuste de imagem ({Unwrap(ex).Message})");
        }
    }

    // ---------------------------------------------------------------- posição na tela
    private void Place()
    {
        if (_host is null)
        {
            return;
        }

        var show = _visible && State == RdpState.Connected;
        if (show)
        {
            Canvas.SetLeft(_host, Math.Round(_bounds.X));
            Canvas.SetTop(_host, Math.Round(_bounds.Y));
            _host.Width = Math.Max(1, Math.Round(_bounds.Width));
            _host.Height = Math.Max(1, Math.Round(_bounds.Height));
        }

        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_host.Visibility != visibility)
        {
            _host.Visibility = visibility;
            if (show)
            {
                BringToFront(_host);
            }
        }
    }

    /// <summary>Janelas nativas vizinhas (a página é outra): garante o controle por cima dela.</summary>
    private static void BringToFront(WindowsFormsHost host)
    {
        host.Dispatcher.BeginInvoke(() =>
        {
            if (host.Handle != IntPtr.Zero)
            {
                SetWindowPos(host.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
            }
        }, DispatcherPriority.Loaded);
    }

    private void SetState(RdpState state, string? message)
    {
        State = state;
        Place();
        StateChanged?.Invoke(state, message);
    }

    private double DpiScale() =>
        PresentationSource.FromVisual(_layer)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    private static (int Width, int Height) DesktopSizeFor(Rect bounds, double scale)
    {
        // O servidor exige largura par; limites do protocolo: 200 a 8192.
        var width = (int)Math.Round(Math.Max(200, bounds.Width) * scale) & ~1;
        var height = (int)Math.Round(Math.Max(200, bounds.Height) * scale);
        return (Math.Clamp(width, 200, 8192), Math.Clamp(height, 200, 8192));
    }

    private static int DesktopScaleFactor(double scale) => (int)Math.Clamp(Math.Round(scale * 100 / 25) * 25, 100, 500);

    private static int DeviceScaleFactor(double scale) => scale >= 1.8 ? 180 : scale >= 1.4 ? 140 : 100;

    private void Ui(Action action)
    {
        if (!_disposed)
        {
            _layer.Dispatcher.BeginInvoke(action);
        }
    }

    private static void TrySet(object? target, string name, object value)
    {
        try
        {
            RdpClientHost.Set(target, name, value);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            // propriedade de versão mais nova do controle: ignora
        }
    }

    private static object? TryGet(RdpClientHost client, string name)
    {
        try
        {
            return client.Get(name);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            return null;
        }
    }

    private static Exception Unwrap(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private void DestroyClient()
    {
        _resizeTimer.Stop();
        _loggedIn = false;
        if (_client is not null)
        {
            try
            {
                if (Convert.ToInt32(_client.Get("Connected") ?? 0) != 0)
                {
                    _userDisconnect = true;
                    _client.Call("Disconnect");
                }
            }
            catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException)
            {
                // já desconectado
            }

            _client.Dispose();
            _client = null;
        }

        if (_host is not null)
        {
            _layer.Children.Remove(_host);
            _host.Dispose();
            _host = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyClient();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Propriedades estendidas do controle (escala de DPI).</summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void set_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, ref object value);

    void get_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, out object value);
}
