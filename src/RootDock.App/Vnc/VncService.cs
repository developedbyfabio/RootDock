using System.Text.Json;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Vnc;

namespace RootDock.App.Vnc;

/// <summary>
/// Abas de VNC: a tela usa o noVNC; aqui ficam a ponte local (WebSocket ↔ TCP) de cada conexão
/// e a senha salva no Cofre do Windows.
/// </summary>
public sealed class VncService
{
    private readonly TerminalWorkspace _workspace;
    private readonly CredentialVault _vault;
    private readonly SessionManager _sessions;
    private readonly Action<string, bool> _notify;
    private readonly Dictionary<string, (SessionEntry Entry, WebSocketTcpBridge? Bridge, bool Connected)> _open = new();

    public VncService(TerminalWorkspace workspace, CredentialVault vault, SessionManager sessions, Action<string, bool> notify)
    {
        _workspace = workspace;
        _vault = vault;
        _sessions = sessions;
        _notify = notify;
    }

    public IEnumerable<SessionEntry> OpenEntries => _open.Values.Select(v => v.Entry);

    public int ConnectedCount => _open.Values.Count(v => v.Connected);

    public int OpenCount => _open.Count;

    public void Open(SessionEntry entry)
    {
        var id = Guid.NewGuid().ToString("N");
        _open[id] = (entry, null, false);
        _sessions.Touch(entry);
        _workspace.NotifySessionsChanged();
        _workspace.Send(new
        {
            type = "openVnc",
            id,
            title = entry.Name,
            env = entry.Environment.ToString(),
            host = entry.Port == 5900 ? entry.Host : $"{entry.Host}:{entry.Port}",
            saved = _sessions.Contains(entry.Id),
        });
        _workspace.FocusTerminal();
    }

    /// <summary>Mensagens da página para as abas de VNC. true = era de VNC.</summary>
    public bool Handle(string type, string? id, JsonElement msg)
    {
        if (id is null || !_open.TryGetValue(id, out var item))
        {
            return type.StartsWith("vnc", StringComparison.Ordinal);
        }

        switch (type)
        {
            case "vncConnect":
            {
                item.Bridge?.Dispose();
                var bridge = WebSocketTcpBridge.Start(item.Entry.Host, item.Entry.Port, "https://rootdock.example");
                _open[id] = (item.Entry, bridge, false);
                string? password = null;
                try
                {
                    password = _vault.Read(CredentialVault.VncPasswordKey(item.Entry.Id));
                }
                catch (Exception ex)
                {
                    Log.Error("VNC: ler senha", ex);
                }

                _workspace.Send(new { type = "vncUrl", id, url = bridge.Url.ToString(), password });
                return true;
            }

            case "vncState":
                _open[id] = (item.Entry, item.Bridge, msg.GetProperty("connected").GetBoolean());
                _workspace.NotifySessionsChanged();
                return true;

            case "vncRemember":
                if (_sessions.Contains(item.Entry.Id) && msg.GetProperty("password").GetString() is { Length: > 0 } secret)
                {
                    try
                    {
                        _vault.Save(CredentialVault.VncPasswordKey(item.Entry.Id), "vnc", secret);
                        _notify($"Senha do VNC salva em {item.Entry.Name} (Cofre do Windows).", false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("VNC: salvar senha", ex);
                    }
                }

                return true;

            case "vncClipboardRequest":
                string? text = null;
                try
                {
                    text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // área de transferência ocupada por outro programa
                }

                if (string.IsNullOrEmpty(text))
                {
                    _notify("A área de transferência não tem texto para enviar.", true);
                }
                else
                {
                    _workspace.Send(new { type = "vncClipboard", id, text });
                    _notify("Texto enviado para a área de transferência do computador remoto: cole lá com Ctrl+V.", false);
                }

                return true;

            case "vncClosed":
                item.Bridge?.Dispose();
                _open.Remove(id);
                _workspace.NotifySessionsChanged();
                return true;

            default:
                return type.StartsWith("vnc", StringComparison.Ordinal);
        }
    }

    public void CloseAll()
    {
        foreach (var (_, bridge, _) in _open.Values)
        {
            bridge?.Dispose();
        }

        _open.Clear();
    }
}
