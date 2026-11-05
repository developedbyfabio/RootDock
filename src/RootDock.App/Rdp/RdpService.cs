using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RootDock.App.Dialogs;
using RootDock.App.Remote;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Smb;

namespace RootDock.App.Rdp;

/// <summary>Aba de RDP ativa (nome e servidor, para o painel de arquivos).</summary>
public sealed record RemoteDesktopInfo(string Id, string Name, string Host);

/// <summary>
/// Abas de Área de Trabalho Remota: pede o login (ou usa o salvo), abre a aba na tela e liga o
/// controle do Windows ao espaço dela. As mensagens da página chegam por <see cref="Handle"/>.
/// </summary>
public sealed class RdpService
{
    private readonly TerminalWorkspace _workspace;
    private readonly Canvas _layer;
    private readonly Window _owner;
    private readonly SessionManager _sessions;
    private readonly SessionLogins _logins;
    private readonly Action<string, bool> _notify;
    private readonly Dictionary<string, RdpSession> _open = new();

    /// <summary>Algo da página está por cima (menu, diálogo, arraste): os controles somem até acabar.</summary>
    private bool _obstructed;

    /// <summary>Abre na página o espaço onde os controles aparecem (senão a página os cobre).</summary>
    private readonly WebViewCutout _cutout;

    public RdpService(TerminalWorkspace workspace, HwndHost webView, Canvas layer, Window owner, CredentialVault vault, SessionManager sessions, Action<string, bool> notify)
    {
        _workspace = workspace;
        _layer = layer;
        _owner = owner;
        _sessions = sessions;
        _logins = new SessionLogins(vault);
        _notify = notify;
        _cutout = new WebViewCutout(webView, layer, () => _open.Values.Select(s => s.Host).OfType<HwndHost>());
    }

    public int ConnectedCount => _open.Values.Count(s => s.IsConnected);

    public int OpenCount => _open.Count;

    /// <summary>Fila e histórico de transferências (gerenciador de arquivos do servidor).</summary>
    public TransferQueue? Transfers { get; set; }

    public Core.History.TransferHistory? TransferHistory { get; set; }

    /// <summary>Aba de RDP com este id (para o painel de arquivos), ou null.</summary>
    public RemoteDesktopInfo? Describe(string? id) =>
        id is not null && _open.TryGetValue(id, out var session) ? new RemoteDesktopInfo(id, session.Entry.Name, session.Entry.Host) : null;

    /// <summary>Arquivos do servidor da aba (compartilhamento do Windows, com o login da sessão).</summary>
    public Task OpenFilesAsync(string id) =>
        _open.TryGetValue(id, out var session) ? OpenFilesAsync(session.Entry, session.Credentials) : Task.CompletedTask;

    /// <summary>
    /// Gerenciador em duas colunas com os arquivos do servidor Windows (SMB, porta 445). Usa o
    /// login informado (ou o do Windows atual, se for o mesmo usuário); recusado, pede outro.
    /// </summary>
    public async Task OpenFilesAsync(SessionEntry entry, RdpCredentials? known)
    {
        if (Transfers is null || TransferHistory is null)
        {
            return;
        }

        var credentials = known;
        while (true)
        {
            SmbFileSystem first;
            try
            {
                _notify($"Abrindo os arquivos de {entry.Host}...", false);
                first = await SmbFileSystem.ConnectAsync(entry.Host, credentials?.Username, credentials?.Password, CancellationToken.None);
            }
            catch (SmbAccessException ex) when (ex.Code != 1219)
            {
                // Login recusado ou sem permissão: pede outro usuário (ex.: um administrador do servidor).
                if (Login(entry, ex.Message, forceAsk: true) is not { } other)
                {
                    return;
                }

                credentials = other;
                continue;
            }
            catch (Exception ex)
            {
                Log.Info($"Arquivos (SMB) {entry.Host}: {ex.Message}");
                await Prompts.InfoAsync("Não foi possível abrir os arquivos do servidor", ex.Message);
                return;
            }

            try
            {
                ShowFiles(entry, first, credentials);
            }
            catch (Exception ex)
            {
                first.Dispose();
                Log.Error($"Abrir gerenciador (SMB) {entry.Host}", ex);
                await Prompts.InfoAsync("Não foi possível abrir o gerenciador de arquivos", ex.Message);
            }

            return;
        }
    }

    private void ShowFiles(SessionEntry entry, SmbFileSystem first, RdpCredentials? credentials)
    {
        SmbFileSystem current = first;
        var gate = new SemaphoreSlim(1, 1);
        var source = new FileManagerSource
        {
            Name = entry.Name,
            IsProduction = entry.Environment == SessionEnvironment.Production,
            StartPath = "/",
            Protocol = "SMB",
            Connect = async () =>
            {
                await gate.WaitAsync();
                try
                {
                    if (!current.IsConnected)
                    {
                        current = await SmbFileSystem.ConnectAsync(entry.Host, credentials?.Username, credentials?.Password, CancellationToken.None);
                    }

                    return current;
                }
                finally
                {
                    gate.Release();
                }
            },
            WindowsPath = e => current.ToUnc(e.FullPath),
            ShowPath = path => current.ToUnc(path),
            ReadPath = typed => SmbFileSystem.FromWindowsPath(first.Host, typed),
            Closed = () => current.Dispose(),
        };
        FileManagerWindow.ShowFor(_owner, source, Transfers!, TransferHistory!);
    }

    /// <summary>Sessões com aba aberta (para reabrir na próxima vez).</summary>
    public IEnumerable<SessionEntry> OpenEntries => _open.Values.Select(s => s.Entry);

    public bool Owns(string id) => _open.ContainsKey(id);

    /// <summary>Abre uma aba de Área de Trabalho Remota (pede o login se não houver senha salva).</summary>
    public void Open(SessionEntry entry, string? splitOf = null)
    {
        if (Login(entry, message: null) is not { } credentials)
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var session = new RdpSession(id, entry, credentials, _layer);
        session.StateChanged += (state, message) =>
        {
            _workspace.Send(new
            {
                type = "rdpState",
                id,
                state = state.ToString().ToLowerInvariant(),
                message,
            });
            _cutout.Invalidate();
            _workspace.NotifySessionsChanged();
        };
        session.LoggedIn += () => Remember(session);
        _open[id] = session;
        _cutout.Watch(true);
        _workspace.NotifySessionsChanged();
        _sessions.Touch(entry);
        _workspace.Send(new
        {
            type = "openRdp",
            id,
            title = entry.Name,
            env = entry.Environment.ToString(),
            host = entry.Port == 3389 ? entry.Host : $"{entry.Host}:{entry.Port}",
            user = credentials.Username,
            splitOf,
        });
        _workspace.FocusTerminal();
    }

    private RdpCredentials? Login(SessionEntry entry, string? message, bool forceAsk = false)
    {
        IReadOnlyList<SavedLogin> saved;
        try
        {
            saved = _logins.List(entry);
        }
        catch (Exception ex)
        {
            Log.Error("RDP: logins salvos", ex);
            saved = [];
        }

        string? Password(string user)
        {
            try
            {
                return _logins.ReadPassword(entry, user);
            }
            catch (Exception ex)
            {
                Log.Error("RDP: ler senha do cofre", ex);
                return null;
            }
        }

        // Login padrão com senha salva (e sem pedir para escolher): conecta direto.
        var ask = forceAsk || message is not null || saved.Count == 0 || (entry.ChooseLoginOnConnect && saved.Count > 1);
        if (!ask && saved.FirstOrDefault(s => s.IsDefault) is { HasPassword: true } login && Password(login.Username) is { } stored)
        {
            return new RdpCredentials(login.Username, stored, Remember: false);
        }

        return RdpLoginWindow.Ask(_owner, entry, saved, Password, message);
    }

    /// <summary>Login aceito: guarda usuário e senha digitados (se a pessoa marcou "Lembrar").</summary>
    private void Remember(RdpSession session)
    {
        var credentials = session.Credentials;
        var entry = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == session.Entry.Id);
        if (entry is null)
        {
            return; // conexão rápida sem sessão salva
        }

        var known = entry.Logins.Contains(credentials.Username, StringComparer.Ordinal);
        if (known && !credentials.Remember)
        {
            return;
        }

        try
        {
            _logins.Save(entry, credentials.Username, credentials.Remember ? credentials.Password : null);
            _sessions.Save();
            session.UpdateCredentials(credentials with { Remember = false });
            if (credentials.Remember && credentials.Password is not null)
            {
                _notify($"Login {credentials.Username} salvo em {entry.Name} (senha no Cofre do Windows).", false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("RDP: salvar login", ex);
            _notify($"Não foi possível salvar o login: {ex.Message}", true);
        }
    }

    /// <summary>Mensagens da página para as abas de RDP. true = era de RDP.</summary>
    public bool Handle(string type, string? id, JsonElement msg)
    {
        switch (type)
        {
            case "rdpObstructed":
                _obstructed = msg.GetProperty("value").GetBoolean();
                foreach (var (sid, last) in _lastBounds)
                {
                    if (_open.TryGetValue(sid, out var open))
                    {
                        open.SetBounds(last.Rect, last.Visible && !_obstructed);
                    }
                }

                return true;
            case "rdpBounds" when id is not null && _open.TryGetValue(id, out var session):
            {
                var rect = new Rect(
                    msg.GetProperty("x").GetDouble(),
                    msg.GetProperty("y").GetDouble(),
                    Math.Max(0, msg.GetProperty("w").GetDouble()),
                    Math.Max(0, msg.GetProperty("h").GetDouble()));
                var visible = msg.GetProperty("visible").GetBoolean();
                _lastBounds[id] = (rect, visible);
                session.SetBounds(rect, visible && !_obstructed);
                return true;
            }

            case "rdpReconnect" when id is not null && _open.TryGetValue(id, out var session):
                if (msg.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.True)
                {
                    if (Login(session.Entry, message: null, forceAsk: true) is not { } credentials)
                    {
                        return true;
                    }

                    session.UpdateCredentials(credentials);
                }

                session.Reconnect();
                return true;
            case "rdpDisconnect" when id is not null && _open.TryGetValue(id, out var session):
                session.Disconnect();
                return true;
            case "rdpFullscreen" when id is not null && _open.TryGetValue(id, out var session):
                session.FullScreen();
                return true;
            case "rdpFiles" when id is not null:
                _ = OpenFilesAsync(id);
                return true;
            case "rdpFocus" when id is not null && _open.TryGetValue(id, out var session):
                session.Focus();
                return true;
            case "rdpClosed" when id is not null && _open.Remove(id, out var session):
                _lastBounds.Remove(id);
                session.Dispose();
                _cutout.Watch(_open.Count > 0);
                _workspace.NotifySessionsChanged();
                return true;
            default:
                return type.StartsWith("rdp", StringComparison.Ordinal);
        }
    }

    private readonly Dictionary<string, (Rect Rect, bool Visible)> _lastBounds = new();

    public void CloseAll()
    {
        foreach (var session in _open.Values)
        {
            session.Dispose();
        }

        _open.Clear();
        _cutout.Watch(false);
    }
}
