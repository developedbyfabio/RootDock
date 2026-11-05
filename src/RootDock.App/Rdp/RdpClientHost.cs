using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RootDock.App.Rdp;

/// <summary>
/// Controle oficial de Área de Trabalho Remota do Windows (mstscax.dll, o mesmo do mstsc),
/// hospedado como ActiveX. As propriedades são acessadas por IDispatch (sem assemblies de
/// interop gerados) e os eventos chegam por um coletor de eventos COM.
/// </summary>
internal sealed class RdpClientHost : AxHost
{
    /// <summary>MsRdpClient9NotSafeForScripting (Windows 8.1+): permite senha e resolução dinâmica.</summary>
    private const string ClassId = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    private ConnectionPointCookie? _cookie;
    private EventSink? _sink;

    public RdpClientHost()
        : base(ClassId)
    {
    }

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action? EnteredFullScreen;
    public event Action? LeftFullScreen;
    public event Action? MinimizeRequested;
    public event Action<int>? FatalError;

    public object? Ocx => GetOcx();

    protected override void CreateSink()
    {
        if (GetOcx() is not { } ocx)
        {
            return;
        }

        try
        {
            _sink = new EventSink(this);
            _cookie = new ConnectionPointCookie(ocx, _sink, typeof(IMsTscAxEvents));
        }
        catch (Exception ex)
        {
            Services.Log.Error("RDP: eventos do controle", ex);
        }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
        _sink = null;
    }

    // ---------------------------------------------------------------- IDispatch
    public void Set(string name, object value) => Set(Ocx, name, value);

    public object? Get(string name) => Get(Ocx, name);

    public object? Call(string name, params object[] args) =>
        Ocx?.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, Ocx, args);

    public static void Set(object? target, string name, object value) =>
        target?.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    public static object? Get(object? target, string name) =>
        target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    /// <summary>Senha: fica numa interface sem IDispatch (não é exposta a scripts).</summary>
    public void SetPassword(string password)
    {
        if (Ocx is IMsTscNonScriptable secure)
        {
            secure.ClearTextPassword = password;
        }
    }

    // ---------------------------------------------------------------- eventos
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EventSink(RdpClientHost owner) : IMsTscAxEvents
    {
        public void OnConnecting() => owner.Connecting?.Invoke();

        public void OnConnected() => owner.Connected?.Invoke();

        public void OnLoginComplete() => owner.LoginComplete?.Invoke();

        public void OnDisconnected(int discReason) => owner.Disconnected?.Invoke(discReason);

        public void OnEnterFullScreenMode() => owner.EnteredFullScreen?.Invoke();

        public void OnLeaveFullScreenMode() => owner.LeftFullScreen?.Invoke();

        public void OnFatalError(int errorCode) => owner.FatalError?.Invoke(errorCode);

        public void OnWarning(int warningCode)
        {
        }

        public void OnRemoteDesktopSizeChange(int width, int height)
        {
        }

        public void OnIdleTimeoutNotification()
        {
        }

        /// <summary>Botão "minimizar" da barra azul da tela cheia.</summary>
        public void OnRequestContainerMinimize() => owner.MinimizeRequested?.Invoke();

        public void OnAuthenticationWarningDisplayed()
        {
        }

        public void OnAuthenticationWarningDismissed()
        {
        }
    }
}

/// <summary>Eventos do controle RDP (MSTSCLib.IMsTscAxEvents).</summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();

    [DispId(2)] void OnConnected();

    [DispId(3)] void OnLoginComplete();

    [DispId(4)] void OnDisconnected(int discReason);

    [DispId(5)] void OnEnterFullScreenMode();

    [DispId(6)] void OnLeaveFullScreenMode();

    [DispId(10)] void OnFatalError(int errorCode);

    [DispId(11)] void OnWarning(int warningCode);

    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);

    [DispId(13)] void OnIdleTimeoutNotification();

    [DispId(14)] void OnRequestContainerMinimize();

    [DispId(18)] void OnAuthenticationWarningDisplayed();

    [DispId(19)] void OnAuthenticationWarningDismissed();
}

/// <summary>Interface "não scriptável" do controle: só o primeiro membro (a senha) é usado.</summary>
[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    string ClearTextPassword { [param: MarshalAs(UnmanagedType.BStr)] set; }
}
