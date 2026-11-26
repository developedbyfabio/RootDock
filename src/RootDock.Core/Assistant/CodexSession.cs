using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RootDock.Core.Assistant;

/// <summary>Como está o Codex (o agente da conta ChatGPT) nesta máquina.</summary>
public sealed record CodexStatus(string? Path, bool LoggedIn, string? Account)
{
    public bool Installed => Path is not null;
}

/// <summary>
/// Uma conversa com o Codex oficial instalado na máquina (o que vem com o aplicativo do ChatGPT,
/// ou o Codex CLI), rodando sem janela. O login é o do próprio Codex, com a conta ChatGPT: o
/// RootDock não vê nem guarda a credencial. As ferramentas dele que agiriam neste computador
/// (shell, navegador, uso do computador, plugins) ficam desligadas; ele só recebe as do RootDock,
/// que agem no servidor da sessão ativa e passam pelas mesmas regras, bloqueios e confirmações.
///
/// Diferente do Claude Code, o Codex não fica aberto entre as mensagens: cada mensagem é um
/// processo ("codex exec", depois "codex exec resume &lt;conversa&gt;"), e a resposta chega em
/// blocos prontos (não letra por letra).
/// </summary>
public sealed class CodexSession(
    string codexPath, string workDirectory, string? model, AssistantToolRunner runner, IAssistantObserver observer, string? effort = null) : IDisposable
{
    /// <summary>
    /// O que o Codex faria neste computador e que aqui não pode: shell, terminal, uso do computador
    /// e do navegador, aplicativos e plugins da pessoa, ganchos, geração e leitura de imagens do
    /// disco, e recursos de sessão que não fazem sentido numa conversa do RootDock.
    /// Nome de recurso que a versão instalada não conhece é ignorado por ela.
    /// </summary>
    internal static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "unified_exec_tty", "computer_use", "browser_use", "browser_use_external",
        "browser_use_full_cdp_access", "apps", "plugins", "remote_plugin", "hooks", "image_generation", "view_image",
        "skill_search", "goals", "sleep_tool", "memories",
    ];

    /// <summary>Acima disto as instruções não cabem com folga na linha de comando do Windows (32 mil caracteres).</summary>
    internal const int MaxInlineInstructionChars = 20_000;

    private readonly McpToolServer _server = new(runner.RunAsync);
    private readonly StringBuilder _errors = new();
    private readonly List<string> _imageFiles = [];
    private string? _threadId;
    private bool _started;
    private Process? _process;

    public bool HasHistory => _started;

    /// <summary>
    /// Procura o codex.exe: variável ROOTDOCK_CODEX_PATH, PATH, o que o aplicativo do ChatGPT/Codex
    /// deixa em %LOCALAPPDATA%\OpenAI\Codex\bin (a pasta tem um código que muda a cada atualização:
    /// vale o mais novo) e a instalação por npm.
    /// </summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("ROOTDOCK_CODEX_PATH") is { Length: > 0 } chosen)
        {
            candidates.Add(chosen.Trim().Trim('"'));
        }

        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        candidates.AddRange(path.Select(d => Path.Combine(d.Trim(), "codex.exe")));
        candidates.AddRange(AppCopies(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin")));
        candidates.AddRange(path.Select(d => Path.Combine(d.Trim(), "codex.cmd")));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
        foreach (var candidate in candidates)
        {
            try
            {
                // Os atalhos de aplicativo da loja em WindowsApps têm zero bytes e não aceitam argumentos de linha de comando.
                if (File.Exists(candidate) && (candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || new FileInfo(candidate).Length > 0))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // entrada inválida no PATH
            }
        }

        return null;
    }

    /// <summary>As cópias do codex.exe que o aplicativo mantém, da mais nova para a mais antiga.</summary>
    internal static IEnumerable<string> AppCopies(string binFolder)
    {
        try
        {
            if (!Directory.Exists(binFolder))
            {
                return [];
            }

            return Directory.EnumerateDirectories(binFolder)
                .Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Instalado? Logado? (pergunta ao próprio Codex: "codex login status").</summary>
    public static async Task<CodexStatus> GetStatusAsync(CancellationToken ct)
    {
        if (Find() is not { } path)
        {
            return new CodexStatus(null, false, null);
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(ClaudeCodeSession.StartInfo(path, Path.GetTempPath(), ["login", "status"]))!;
            using var kill = limit.Token.Register(() => Kill(process));
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return ParseStatus(path, output + "\n" + await errors.ConfigureAwait(false), process.ExitCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Não respondeu a tempo: o primeiro pedido dirá se falta login.
            return new CodexStatus(path, true, null);
        }
    }

    internal static CodexStatus ParseStatus(string path, string text, int exitCode)
    {
        var logged = exitCode == 0 && text.Contains("Logged in", StringComparison.OrdinalIgnoreCase) &&
                     !text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase);
        var how = text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "conta ChatGPT"
            : text.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "chave de API"
            : null;
        return new CodexStatus(path, logged, logged ? how : null);
    }

    /// <summary>
    /// Os modelos que a conta pode usar, perguntados ao próprio Codex ("codex debug models"), na
    /// ordem em que ele os mostra. Lança ChatModelException se ele não responder.
    /// </summary>
    public static async Task<IReadOnlyList<ClaudeCodeModel>> ListModelsAsync(CancellationToken ct)
    {
        var path = Find() ?? throw new ChatModelException("O Codex não está instalado nesta máquina.");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = Process.Start(ClaudeCodeSession.StartInfo(path, Path.GetTempPath(), ["debug", "models"]))!;
            using var kill = limit.Token.Register(() => Kill(process));
            process.ErrorDataReceived += (_, _) => { }; // lê e descarta: stderr cheio travaria o processo
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return ParseModels(output) is { Count: > 0 } models
                ? models
                : throw new ChatModelException("O Codex não informou os modelos (versão antiga?). Atualize o aplicativo do ChatGPT ou o Codex.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ChatModelException("O Codex não respondeu com a lista de modelos.", ex);
        }
    }

    /// <summary>Lê o catálogo de "codex debug models": só os modelos que ele lista para escolha, por prioridade.</summary>
    internal static IReadOnlyList<ClaudeCodeModel>? ParseModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = new List<(int Priority, ClaudeCodeModel Model)>();
            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "slug") is not { Length: > 0 } slug || Text(item, "visibility") is { } visibility && visibility != "list" ||
                    models.Any(m => m.Model.Value == slug))
                {
                    continue;
                }

                var efforts = item.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
                    ? levels.EnumerateArray().Select(l => Text(l, "effort")).OfType<string>().ToList()
                    : [];
                var priority = item.TryGetProperty("priority", out var order) && order.TryGetInt32(out var number) ? number : int.MaxValue;
                models.Add((priority, new ClaudeCodeModel(slug, Text(item, "display_name") is { Length: > 0 } name ? name : slug, slug, efforts)));
            }

            return models.OrderBy(m => m.Priority).Select(m => m.Model).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Envia a mensagem e acompanha a resposta até o fim. Cancelar encerra o processo; a conversa
    /// continua na próxima mensagem (o Codex guarda o histórico dele e o RootDock o retoma pelo id).
    /// </summary>
    public async Task SendAsync(string system, string userText, IReadOnlyList<ChatImage> images, CancellationToken ct)
    {
        _server.TurnToken = ct;
        // As ferramentas de web são as do RootDock (passam pela confirmação da pessoa no modo "perguntar tudo").
        _server.Hidden = null;

        var folder = Path.Combine(workDirectory, "codex");
        Directory.CreateDirectory(folder);
        DeleteImages();
        foreach (var image in images)
        {
            var file = Path.Combine(folder, "imagem-" + Guid.NewGuid().ToString("N")[..10] + (image.MediaType == "image/png" ? ".png" : ".jpg"));
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(image.Base64), ct).ConfigureAwait(false);
            _imageFiles.Add(file);
        }

        var viaShell = ClaudeCodeSession.Launcher(codexPath).ViaShell;
        var (inline, overflow) = SplitInstructions(system, viaShell ? 0 : MaxInlineInstructionChars);
        var arguments = BuildArguments(_started ? _threadId : null, model, effort, _server.Url, inline, _imageFiles);
        var info = ClaudeCodeSession.StartInfo(codexPath, folder, arguments);
        info.Environment["ROOTDOCK_MCP_TOKEN"] = _server.Token;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ChatModelException("Não foi possível iniciar o Codex.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ChatModelException("Não foi possível iniciar o Codex: " + ex.Message, ex);
        }

        _process = process;
        lock (_errors)
        {
            _errors.Clear();
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_errors)
            {
                _errors.AppendLine(e.Data);
                if (_errors.Length > 8000)
                {
                    _errors.Remove(0, _errors.Length - 4000);
                }
            }
        };
        process.BeginErrorReadLine();
        using var kill = ct.Register(() => Kill(process));
        try
        {
            // A pergunta vai pela entrada padrão (sem limite de tamanho nem problema de aspas).
            var prompt = overflow.Length > 0
                ? "[Instruções do RootDock para esta conversa]\n" + overflow + "\n[Fim das instruções]\n\n" + userText
                : userText;
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
            _started = true;

            var turn = new TurnState();
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                Handle(line, turn);
            }

            ct.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (turn.Failure is { } failure)
            {
                throw new ChatModelException(Explain(failure));
            }

            if (!turn.Completed)
            {
                string error;
                lock (_errors)
                {
                    error = LastLines(_errors.ToString(), 600);
                }

                throw new ChatModelException(Explain(error.Length > 0 ? "O Codex encerrou: " + error : "O Codex encerrou sem responder."));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ChatModelException("A conversa com o Codex caiu: " + ex.Message, ex);
        }
        finally
        {
            Kill(process);
            process.Dispose();
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            DeleteImages();
        }
    }

    private sealed class TurnState
    {
        public bool Completed { get; set; }

        public string? Failure { get; set; }

        /// <summary>O último bloco emitido foi texto (o próximo texto começa em parágrafo novo).</summary>
        public bool LastWasText { get; set; }
    }

    /// <summary>Um evento (uma linha JSON) do "codex exec --json".</summary>
    private void Handle(string line, TurnState turn)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // linha de log, não é evento
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (Text(root, "type"))
            {
                case "thread.started":
                    _threadId = Text(root, "thread_id") ?? _threadId;
                    break;

                case "item.started" when root.TryGetProperty("item", out var started) && Text(started, "type") != "agent_message":
                    turn.LastWasText = false; // uma ferramenta entra na conversa entre dois textos
                    break;

                case "item.completed" when root.TryGetProperty("item", out var item):
                    switch (Text(item, "type"))
                    {
                        case "agent_message" when Text(item, "text") is { Length: > 0 } text:
                            observer.Text((turn.LastWasText ? "\n\n" : "") + text);
                            turn.LastWasText = true;
                            break;

                        // Ferramenta que não é do RootDock (não deveria existir: as locais estão desligadas).
                        case "mcp_tool_call" when Text(item, "server") is { } server && server != McpToolServer.ServerName:
                        case "command_execution":
                            turn.LastWasText = false;
                            break;

                        case "error" when Text(item, "message") is { Length: > 0 } message:
                            turn.Failure ??= message;
                            break;
                    }

                    break;

                case "turn.completed":
                    turn.Completed = true;
                    turn.Failure = null; // avisos no meio do caminho não são falha se a rodada terminou
                    break;

                case "turn.failed":
                    turn.Failure = root.TryGetProperty("error", out var error) && Text(error, "message") is { Length: > 0 } reason
                        ? reason
                        : "O Codex não conseguiu responder.";
                    break;

                case "error" when Text(root, "message") is { Length: > 0 } fatal:
                    turn.Failure ??= fatal;
                    break;
            }
        }
    }

    /// <summary>
    /// A linha de comando de uma rodada. As ferramentas locais do Codex saem (<see cref="DisabledFeatures"/>);
    /// a configuração da pessoa (outros servidores MCP, plugins, regras) não é carregada; o que sobra de
    /// local roda em modo somente leitura. As ferramentas do RootDock entram já aprovadas: quem pergunta
    /// à pessoa é o RootDock.
    /// </summary>
    internal static List<string> BuildArguments(string? threadId, string? model, string? effort, string mcpUrl, string instructions, IReadOnlyList<string> imageFiles)
    {
        var arguments = new List<string> { "exec" };
        if (threadId is { Length: > 0 })
        {
            arguments.AddRange(["resume", threadId]);
        }

        arguments.AddRange(["--json", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check"]);
        if (threadId is null)
        {
            arguments.AddRange(["--sandbox", "read-only"]); // ao retomar, a conversa mantém a política com que nasceu
        }

        foreach (var feature in DisabledFeatures)
        {
            arguments.AddRange(["--disable", feature]);
        }

        arguments.AddRange(
        [
            "-c", $"mcp_servers.{McpToolServer.ServerName}.url={Toml(mcpUrl)}",
            // A senha entra por variável de ambiente, não fica na linha de comando.
            "-c", $"mcp_servers.{McpToolServer.ServerName}.bearer_token_env_var={Toml("ROOTDOCK_MCP_TOKEN")}",
            "-c", $"mcp_servers.{McpToolServer.ServerName}.default_tools_approval_mode={Toml("approve")}",
            // A pessoa pode demorar para confirmar uma ação na tela do RootDock.
            "-c", $"mcp_servers.{McpToolServer.ServerName}.tool_timeout_sec=86400",
        ]);
        if (instructions.Length > 0)
        {
            arguments.AddRange(["-c", "developer_instructions=" + Toml(instructions)]);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.AddRange(["--model", model.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            arguments.AddRange(["-c", "model_reasoning_effort=" + Toml(effort.Trim())]);
        }

        foreach (var file in imageFiles)
        {
            arguments.AddRange(["--image", file]);
        }

        return arguments;
    }

    /// <summary>
    /// Divide as instruções entre o que cabe na linha de comando e o resto (que segue junto da
    /// pergunta). O corte é numa linha em branco, para não partir uma regra no meio.
    /// </summary>
    internal static (string Inline, string Overflow) SplitInstructions(string system, int max)
    {
        if (system.Length <= max)
        {
            return (system, "");
        }

        if (max <= 0)
        {
            return ("", system);
        }

        var cut = system.LastIndexOf("\n\n", max, StringComparison.Ordinal);
        if (cut < max / 2)
        {
            cut = system.LastIndexOf('\n', max);
        }

        if (cut <= 0)
        {
            cut = max;
        }

        return (system[..cut].TrimEnd(), system[cut..].Trim());
    }

    /// <summary>Texto como valor TOML (entre aspas, numa linha só), que é como o "-c chave=valor" do Codex o lê.</summary>
    internal static string Toml(string value)
    {
        var text = new StringBuilder(value.Length + 16).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                    {
                        text.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }

    /// <summary>Erro do Codex com a orientação do que fazer quando é login, limite da conta ou instalação.</summary>
    private string Explain(string error)
    {
        var text = error.Length > 700 ? error[..700] + "..." : error;
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("login", StringComparison.OrdinalIgnoreCase) || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "O Codex pediu login. Abra \"Trocar de provedor ou chave...\" no botão do modelo, abaixo do campo de mensagem, e clique em Entrar.\n" + text;
        }

        if (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("quota", StringComparison.OrdinalIgnoreCase) || text.Contains("429", StringComparison.Ordinal))
        {
            return "A conta ChatGPT chegou ao limite de uso do Codex por agora. Espere o limite renovar ou troque de modelo ou de provedor.\n" + text;
        }

        return _started && _threadId is not null
            ? text
            : text + $"\n\nO Codex usado foi: {codexPath}. Se o erro continuar, atualize o aplicativo do ChatGPT (ou o Codex) e abra o RootDock de novo.";
    }

    private static string LastLines(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length > max ? "..." + trimmed[^max..] : trimmed;
    }

    /// <summary>O Codex não fica aberto entre as mensagens: nada a reiniciar, as instruções novas valem na próxima.</summary>
    public void Restart()
    {
    }

    private void DeleteImages()
    {
        foreach (var file in _imageFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // fica para trás na pasta de dados do RootDock
            }
        }

        _imageFiles.Clear();
    }

    public void Dispose()
    {
        if (_process is { } process)
        {
            Kill(process);
        }

        _server.Dispose();
        DeleteImages();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // já encerrado
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
