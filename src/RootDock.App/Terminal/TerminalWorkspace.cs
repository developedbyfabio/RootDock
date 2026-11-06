using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RootDock.App.Services;
using RootDock.Core.Sessions;

namespace RootDock.App.Terminal;

public sealed record RemoteServices(TransferQueue Transfers, Remote.RemoteEditService Editor, Action<string, bool> Notify, TunnelService Tunnels);

/// <summary>
/// Área de terminais: um único WebView2 com xterm.js hospeda todas as abas e divisões
/// (menos memória e nenhum problema de sobreposição de janelas nativas).
/// </summary>
public sealed class TerminalWorkspace
{
    private const string VirtualHost = "rootdock.example";
    private const int IdLength = 32;

    private readonly WebView2 _webView;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly List<string> _queued = new();
    private readonly HashSet<TerminalSession> _dirty = new();
    private readonly object _flushGate = new();
    private bool _flushQueued;
    private bool _ready;

    // Perguntas de login abertas (uma por painel) e cartões de oferta; só na thread de interface.
    private readonly Dictionary<string, TaskCompletionSource<string?>> _prompts = new();
    private readonly Dictionary<string, (string SessionId, TaskCompletionSource<string?> Answer)> _offers = new();

    public TerminalWorkspace(WebView2 webView)
    {
        _webView = webView;
        _dispatcher = webView.Dispatcher;
    }

    public SessionConnector? Connector { get; set; }

    /// <summary>Serviços compartilhados pelos painéis remotos (transferências, edição).</summary>
    public RemoteServices? Services { get; set; }

    internal Dispatcher Dispatcher => _dispatcher;

    public event Action<TerminalSession?>? ActiveSessionChanged;

    public event Action? SessionsChanged;

    public event Action? NewLocalRequested;

    public event Action? QuickConnectRequested;

    /// <summary>Pasta das gravações de terminal (.cast).</summary>
    public string? RecordingsDirectory { get; set; }

    /// <summary>Botão "Reabrir" da tela inicial.</summary>
    public event Action? RestoreRequested;

    /// <summary>Ctrl+Shift+H no terminal: abrir o histórico de comandos.</summary>
    public event Action? HistoryRequested;

    /// <summary>Comandos digitados (para a busca no histórico).</summary>
    public Core.History.CommandHistory? History { get; set; }

    /// <summary>Um comando digitado no prompt foi executado (o mesmo que vai para o histórico): gravação de macro.</summary>
    public event Action<TerminalSession, string>? CommandRan;

    public event Action<int>? FontSizeChanged;

    /// <summary>Botão/atalho da biblioteca de comandos (canto superior direito do terminal).</summary>
    public event Action? CommandsToggleRequested;

    public TerminalSession? ActiveSession { get; private set; }

    /// <summary>Painel ativo na tela (terminal, editor, RDP, VNC...), ou null sem abas.</summary>
    public string? ActivePaneId { get; private set; }

    /// <summary>Editor de arquivos (abas do Monaco).</summary>
    public Remote.EditorService? Editors { get; set; }

    /// <summary>Salva o conteúdo do terminal num arquivo escolhido pelo usuário.</summary>
    private void ExportBuffer(string title, string format, string content)
    {
        var safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salvar o conteúdo do terminal",
            FileName = $"{(safe.Length > 0 ? safe : "terminal")} {DateTime.Now:yyyy-MM-dd HH-mm}.{format}",
            Filter = format == "html" ? "Página HTML (*.html)|*.html" : "Texto (*.txt)|*.txt|Log (*.log)|*.log",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
        {
            FocusTerminal();
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(format == "txt"));
            Services?.Notify($"Terminal salvo em {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Salvar terminal", ex);
            Services?.Notify($"Não foi possível salvar: {ex.Message}", true);
        }

        FocusTerminal();
    }

    /// <summary>Print do código: copiar a imagem, salvar PNG ou exportar PDF (o PNG vem da tela).</summary>
    private void ExportSnap(JsonElement msg)
    {
        var action = msg.GetProperty("action").GetString() ?? "copy";
        var title = msg.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var scale = msg.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 2;
        ImageExportResult result;
        try
        {
            var png = Convert.FromBase64String(msg.GetProperty("png").GetString() ?? "");
            result = action switch
            {
                "save" => ImageExport.SavePng(png, title),
                "pdf" => ImageExport.SavePdf(png, title, scale),
                _ => ImageExport.Copy(png),
            };
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Log.Error($"Print do código ({action})", ex);
            result = new ImageExportResult(false, action == "copy"
                ? $"Não foi possível copiar: {ex.Message}"
                : $"Não foi possível salvar: {ex.Message}");
        }

        Send(new { type = "snapResult", action, ok = result.Ok, message = result.Message, path = result.Path, cancelled = result.Cancelled });
        _webView.Focus(); // volta o teclado para o diálogo do print (Esc, Ctrl+C...)
    }

    /// <summary>Mensagem de controle para a tela (JSON).</summary>
    public void Send(object payload) => Post(JsonSerializer.Serialize(payload));

    /// <summary>Terminais, Áreas de Trabalho Remotas e VNC abertos (editores e reproduções não contam).</summary>
    public int OpenCount => _sessions.Count + (Rdp?.OpenCount ?? 0) + (Vnc?.OpenCount ?? 0);

    /// <summary>Abas de RDP/VNC abriram, fecharam ou mudaram de estado (atualiza a barra de status).</summary>
    internal void NotifySessionsChanged() => SessionsChanged?.Invoke();

    public IEnumerable<TerminalSession> Sessions => _sessions.Values;

    public int ConnectedCount => _sessions.Values.Count(s => s.State == TerminalState.Connected) + (Rdp?.ConnectedCount ?? 0) + (Vnc?.ConnectedCount ?? 0);

    /// <summary>Abas de VNC.</summary>
    public Vnc.VncService? Vnc { get; set; }

    /// <summary>Abas de Área de Trabalho Remota.</summary>
    public Rdp.RdpService? Rdp { get; set; }

    /// <param name="environment">Ambiente já em criação (começa na abertura do app, em paralelo com a janela).</param>
    /// <param name="bootTheme">Tema aplicado já na criação do documento (sem piscar a paleta padrão antes).</param>
    public async Task InitializeAsync(Task<CoreWebView2Environment> environment, int fontSize, object? bootTheme = null)
    {
        RootDock.App.Services.StartupTrace.Mark("controle WebView2");
        await _webView.EnsureCoreWebView2Async(await environment);
        RootDock.App.Services.StartupTrace.Mark("WebView2 pronto");
        var core = _webView.CoreWebView2;

        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        if (bootTheme is not null)
        {
            // Cores do tema antes do primeiro desenho da página: sem isso, quem usa o tema claro via a
            // paleta escura padrão por um instante a cada abertura (o tema só chegava depois do "ready").
            var json = JsonSerializer.Serialize(bootTheme);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__bootTheme=" + json + ";(function(m){var r=document.documentElement;var v=m.vars||{};" +
                "for(var k in v){r.style.setProperty(k,v[k]);}})(window.__bootTheme);");
        }

        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(VirtualHost, root, CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += (_, e) =>
        {
            // A página do terminal nunca navega para fora; links abrem no navegador padrão.
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.ProcessFailed += OnProcessFailed;

        CurrentFontSize = fontSize;
        _queued.Insert(0, JsonSerializer.Serialize(new { type = "settings", fontSize }));
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    /// <param name="splitOf">Painel ao lado do qual abrir (terminal ou editor).</param>
    /// <param name="reuseFrom">Terminal cujo login reaproveitar (padrão: o próprio <paramref name="splitOf"/>).</param>
    public TerminalSession Open(SessionEntry entry, string? splitOf = null, string direction = "v", TerminalSession? reuseFrom = null)
    {
        var connector = Connector ?? throw new InvalidOperationException("Workspace sem conector.");
        var id = Guid.NewGuid().ToString("N");
        var session = new TerminalSession(id, entry, this, connector);
        var source = reuseFrom ?? (splitOf is not null && _sessions.TryGetValue(splitOf, out var s) ? s : null);
        if (source is not null && source.Entry.Id == entry.Id)
        {
            // Dividir a tela abre outro terminal no mesmo servidor com o mesmo login, sem perguntar.
            session.ReuseCredentials = source.Credentials;
            session.ReuseJumps = source.Jumps;
        }

        _sessions[id] = session;
        Post(JsonSerializer.Serialize(new
        {
            type = "open",
            id,
            title = entry.Name,
            env = entry.Environment.ToString(),
            splitOf,
            dir = direction,
        }));
        _webView.Focus();
        SessionsChanged?.Invoke();
        return session;
    }

    /// <summary>A página foi recarregada depois de uma falha: abas de editor, RDP e VNC precisam ser descartadas.</summary>
    public event Action? PageReloaded;

    private bool _recovering;

    /// <summary>Tamanho de fonte atual dos terminais (para reabrir a página igual).</summary>
    public int CurrentFontSize { get; private set; } = 14;

    /// <summary>
    /// Processo de desenho do WebView2 caiu (falta de memória, driver de vídeo) ou travou: recarrega a
    /// página. As conexões continuam vivas no C#; os terminais voltam como abas quando ela ficar pronta.
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Info($"WebView2 processo falhou: {e.ProcessFailedKind} ({e.Reason}, código {e.ExitCode})");
        if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
        {
            return; // GPU e utilitários o próprio WebView2 reinicia; o navegador inteiro não tem volta aqui
        }

        _ready = false;
        _recovering = true;
        _queued.Clear();
        foreach (var session in _sessions.Values)
        {
            session.ResetFlow(); // o que estava a caminho se perdeu; nada pode ficar esperando "resume"
        }

        try
        {
            _webView.CoreWebView2?.Reload();
        }
        catch (Exception ex)
        {
            Log.Error("Recarregar a página após falha", ex);
        }
    }

    private void ReopenAfterReload()
    {
        Post(JsonSerializer.Serialize(new { type = "settings", fontSize = CurrentFontSize }));
        foreach (var session in _sessions.Values.ToList())
        {
            Post(JsonSerializer.Serialize(new { type = "open", id = session.Id, title = session.Entry.Name, env = session.Entry.Environment.ToString(), splitOf = (string?)null, dir = "v" }));
            session.WriteNotice("A área dos terminais foi recarregada após uma falha do WebView2. A conexão continua; o histórico anterior da tela se perdeu.");
            NotifyState(session);
        }

        PageReloaded?.Invoke();
        SessionsChanged?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var session in _sessions.Values.ToList())
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    public void FocusTerminal() => _webView.Focus();

    /// <summary>Mostra no botão da biblioteca se o painel está aberto.</summary>
    public void SetCommandsPanelState(bool open) => Post(JsonSerializer.Serialize(new { type = "commandsPanel", open }));

    /// <summary>Coloca texto no prompt do terminal sem executar (respeita o modo de colagem do shell).</summary>
    public void PasteInto(TerminalSession session, string text) =>
        Post(JsonSerializer.Serialize(new { type = "paste", id = session.Id, text }));

    /// <summary>
    /// Digita no terminal como se fosse o teclado (passa pelo xterm, que marca a linha do comando).
    /// </summary>
    public void TypeInto(TerminalSession session, string text) =>
        Post(JsonSerializer.Serialize(new { type = "input", id = session.Id, data = text }));

    /// <summary>
    /// Coloca o painel em modo de pergunta: a próxima linha digitada volta como resposta
    /// (sem eco quando é senha). O texto da pergunta já foi escrito pela sessão.
    /// </summary>
    internal Task<string?> PromptAsync(TerminalSession session, bool echo, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested)
            {
                answer.TrySetResult(null);
                return;
            }

            if (_prompts.Remove(session.Id, out var previous))
            {
                previous.TrySetResult(null);
            }

            _prompts[session.Id] = answer;
            Post(JsonSerializer.Serialize(new { type = "prompt", id = session.Id, echo }));
        });

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(() => _dispatcher.BeginInvoke(() => EndPrompt(session.Id, answer)));
            _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        return answer.Task;
    }

    private void EndPrompt(string sessionId, TaskCompletionSource<string?> answer)
    {
        if (_prompts.TryGetValue(sessionId, out var current) && current == answer)
        {
            _prompts.Remove(sessionId);
            Post(JsonSerializer.Serialize(new { type = "promptEnd", id = sessionId }));
        }

        answer.TrySetResult(null);
    }

    /// <summary>Cartão com botões no canto do painel (ex.: lembrar usuário e senha).</summary>
    internal Task<string?> OfferAsync(TerminalSession session, string text, IReadOnlyList<OfferAction> actions)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offerId = Guid.NewGuid().ToString("N");
        _dispatcher.BeginInvoke(() =>
        {
            if (!_sessions.ContainsKey(session.Id))
            {
                answer.TrySetResult(null);
                return;
            }

            _offers[offerId] = (session.Id, answer);
            Post(JsonSerializer.Serialize(new
            {
                type = "offer",
                id = session.Id,
                offerId,
                text,
                actions = actions.Select(a => new { id = a.Id, label = a.Label, primary = a.Primary }),
            }));
        });
        return answer.Task;
    }

    private void CancelPending(string sessionId)
    {
        if (_prompts.Remove(sessionId, out var prompt))
        {
            prompt.TrySetResult(null);
        }

        foreach (var (offerId, offer) in _offers.Where(o => o.Value.SessionId == sessionId).ToList())
        {
            _offers.Remove(offerId);
            offer.Answer.TrySetResult(null);
        }
    }

    /// <summary>Organiza em grade todos os painéis da aba deste terminal.</summary>
    public void ArrangeGrid(TerminalSession session) =>
        Post(JsonSerializer.Serialize(new { type = "arrange", id = session.Id, mode = "grid" }));

    /// <summary>Leva o foco do teclado para o painel do terminal.</summary>
    public void Focus(TerminalSession session)
    {
        Post(JsonSerializer.Serialize(new { type = "focus", id = session.Id }));
        _webView.Focus();
    }

    /// <summary>Chamado de threads de fundo quando um painel tem texto novo.</summary>
    internal void ScheduleFlush(TerminalSession session)
    {
        lock (_flushGate)
        {
            _dirty.Add(session);
            if (_flushQueued)
            {
                return;
            }

            _flushQueued = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, FlushDirty);
    }

    internal void NotifyState(TerminalSession session)
    {
        void Send()
        {
            if (!_sessions.ContainsKey(session.Id))
            {
                return;
            }

            // Garante que o texto pendente chegue antes da mudança de estado.
            FlushDirty();
            var state = session.State switch
            {
                TerminalState.Connected => "connected",
                TerminalState.Closed => "closed",
                _ => "connecting",
            };
            Post(JsonSerializer.Serialize(new { type = "state", id = session.Id, state }));
            SessionsChanged?.Invoke();
            if (session == ActiveSession)
            {
                ActiveSessionChanged?.Invoke(session);
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Send();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, Send);
        }
    }

    private void FlushDirty()
    {
        List<TerminalSession> dirty;
        lock (_flushGate)
        {
            _flushQueued = false;
            if (_dirty.Count == 0)
            {
                return;
            }

            dirty = _dirty.ToList();
            _dirty.Clear();
        }

        foreach (var session in dirty)
        {
            var text = session.TakePending();
            if (text.Length > 0 && _sessions.ContainsKey(session.Id))
            {
                session.Recorder?.Output(text);

                // Lotes grandes (UI ocupada durante saída intensa) vão em partes: uma mensagem gigante
                // custa cópias enormes no processo do navegador e chega de uma vez ao xterm.
                const int slice = 1 << 20;
                if (text.Length <= slice)
                {
                    Post("o" + session.Id + text);
                }
                else
                {
                    for (var i = 0; i < text.Length; i += slice)
                    {
                        Post(string.Concat("o", session.Id, text.AsSpan(i, Math.Min(slice, text.Length - i))));
                    }
                }
            }
        }
    }

    private void Post(string message)
    {
        if (!_ready || _webView.CoreWebView2 is null)
        {
            _queued.Add(message);
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsString(message);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (raw.Length > IdLength && (raw[0] == 'i' || raw[0] == 'b'))
        {
            if (_sessions.TryGetValue(raw.Substring(1, IdLength), out var target))
            {
                var data = raw[(1 + IdLength)..];
                if (raw[0] == 'i')
                {
                    target.SendInput(data);
                }
                else
                {
                    target.SendBinary(data);
                }
            }

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            HandleControl(doc.RootElement);
        }
        catch (JsonException ex)
        {
            Log.Error("Mensagem inválida do terminal", ex);
        }
    }

    private void HandleControl(JsonElement msg)
    {
        var type = msg.GetProperty("type").GetString();
        var id = msg.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
        var session = id is not null && _sessions.TryGetValue(id, out var s) ? s : null;
        if (type is not null && type.StartsWith("rdp", StringComparison.Ordinal) && Rdp?.Handle(type, id, msg) == true)
        {
            return;
        }

        if (type is not null && type.StartsWith("vnc", StringComparison.Ordinal) && Vnc?.Handle(type, id, msg) == true)
        {
            return;
        }

        switch (type)
        {
            case "ready":
                _ready = true;
                RootDock.App.Services.StartupTrace.Complete("página pronta");
                if (_recovering)
                {
                    _recovering = false;
                    ReopenAfterReload();
                }

                foreach (var queued in _queued)
                {
                    _webView.CoreWebView2.PostWebMessageAsString(queued);
                }

                _queued.Clear();
                break;

            case "resize":
                session?.OnResize(msg.GetProperty("cols").GetInt32(), msg.GetProperty("rows").GetInt32());
                break;

            case "closeRequest":
                if (session is not null)
                {
                    CancelPending(session.Id);
                    _sessions.Remove(session.Id);
                    session.Dispose();
                    if (ActiveSession == session)
                    {
                        // A tela avisa o novo painel ativo logo em seguida; até lá, nada de dados da aba fechada.
                        ActiveSession = null;
                        ActiveSessionChanged?.Invoke(null);
                    }

                    SessionsChanged?.Invoke();
                }

                break;

            case "reconnect":
                session?.Reconnect();
                break;

            case "editorSave" when id is not null:
                _ = Editors?.SaveAsync(id, msg.GetProperty("content").GetString() ?? "",
                    msg.TryGetProperty("force", out var force) && force.ValueKind == JsonValueKind.True);
                break;

            case "editorReload" when id is not null:
                _ = Editors?.SendServerVersionAsync(id, reload: true);
                break;

            case "editorCompare" when id is not null:
                _ = Editors?.SendServerVersionAsync(id, reload: false);
                break;

            case "record" when session is not null && RecordingsDirectory is not null:
                if (session.Recorder is { } recorder)
                {
                    var duration = recorder.Elapsed;
                    var saved = session.StopRecording();
                    Send(new { type = "recording", id = session.Id, on = false });
                    Services?.Notify($"Gravação salva ({duration:mm\\:ss}): {Path.GetFileName(saved)}. Ferramentas → Gravações para assistir.", false);
                }
                else
                {
                    try
                    {
                        session.StartRecording(RecordingsDirectory);
                        Send(new { type = "recording", id = session.Id, on = true });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Services?.Notify($"Não foi possível gravar: {ex.Message}", true);
                    }
                }

                break;

            case "restoreWorkspace":
                RestoreRequested?.Invoke();
                break;

            case "commandRan" when session is not null:
                var ran = msg.GetProperty("command").GetString();
                if (!string.IsNullOrWhiteSpace(ran))
                {
                    var where = string.IsNullOrEmpty(session.Entry.Host) ? session.Entry.Endpoint : session.Entry.Host;
                    _ = Task.Run(() => History?.Add(session.Entry.Id, session.Entry.Name, where, ran));
                    CommandRan?.Invoke(session, ran.Trim());
                }

                break;

            case "exportBuffer":
                ExportBuffer(
                    msg.GetProperty("title").GetString() ?? "terminal",
                    msg.GetProperty("format").GetString() == "html" ? "html" : "txt",
                    msg.GetProperty("content").GetString() ?? "");
                break;

            case "editorDirty" when id is not null:
                Editors?.SetDirty(id, msg.GetProperty("dirty").GetBoolean());
                break;

            case "editorSaveLocal" when id is not null:
                Editors?.SaveLocalCopy(id, msg.GetProperty("name").GetString() ?? "arquivo.txt", msg.GetProperty("content").GetString() ?? "");
                break;

            case "editorClosed" when id is not null:
                Editors?.Close(id); // o activeChanged seguinte atualiza a sessão ativa
                break;

            case "promptAnswer":
                if (id is not null && _prompts.Remove(id, out var pending))
                {
                    var cancelled = msg.TryGetProperty("cancel", out var c) && c.ValueKind == JsonValueKind.True;
                    pending.TrySetResult(cancelled ? null : msg.GetProperty("value").GetString() ?? "");
                }

                break;

            case "offerAnswer":
                if (msg.GetProperty("offerId").GetString() is { } offerKey && _offers.Remove(offerKey, out var offered))
                {
                    var choice = msg.TryGetProperty("choice", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
                    offered.Answer.TrySetResult(choice);
                }

                break;

            case "split":
            {
                var direction = msg.GetProperty("dir").GetString() == "h" ? "h" : "v";
                if (session is not null)
                {
                    Open(session.Entry, session.Id, direction);
                }
                else if (id is not null && Editors?.OwnerOf(id, _sessions.Values) is { } owner)
                {
                    // Dividir a partir de um arquivo aberto: terminal do mesmo servidor ao lado dele.
                    Open(owner.Entry, id, direction, reuseFrom: owner);
                }

                break;
            }

            case "copy":
                SetClipboard(msg.GetProperty("text").GetString() ?? "");
                break;

            case "snapExport":
                ExportSnap(msg);
                break;

            case "snapReveal":
                if (msg.GetProperty("path").GetString() is { Length: > 0 } shot && File.Exists(shot))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{shot}\"");
                }

                break;

            case "pasteRequest":
                if (session is not null)
                {
                    var text = GetClipboard();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Post(JsonSerializer.Serialize(new { type = "paste", id = session.Id, text }));
                    }
                }

                break;

            case "pause":
                session?.PauseOutput();
                break;

            case "resume":
                session?.ResumeOutput();
                break;

            case "ack":
                session?.Acknowledge(msg.GetProperty("n").GetInt64());
                break;

            case "activeChanged":
                // Aba de editor ativa: vale o terminal dono do arquivo (status, comandos).
                ActivePaneId = id;
                ActiveSession = session ?? (id is not null ? Editors?.OwnerOf(id, _sessions.Values) : null);
                foreach (var each in _sessions.Values)
                {
                    each.SetMonitorActive(each == ActiveSession);
                }

                ActiveSessionChanged?.Invoke(ActiveSession);
                break;

            case "newLocal":
                NewLocalRequested?.Invoke();
                break;

            case "command":
                switch (msg.GetProperty("name").GetString())
                {
                    case "quickConnect":
                        QuickConnectRequested?.Invoke();
                        break;
                    case "history":
                        HistoryRequested?.Invoke();
                        break;
                }

                break;

            case "toggleCommands":
                CommandsToggleRequested?.Invoke();
                break;

            case "fontSize":
                CurrentFontSize = msg.GetProperty("size").GetInt32();
                FontSizeChanged?.Invoke(CurrentFontSize);
                break;

            case "openUrl":
                OpenExternal(msg.GetProperty("url").GetString());
                break;
        }
    }

    private static void OpenExternal(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static void SetClipboard(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        // A área de transferência pode estar ocupada por outro programa por alguns milissegundos.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string? GetClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }
}
