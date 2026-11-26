using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RootDock.Core.Assistant;

/// <summary>Como está o Claude Code nesta máquina.</summary>
public sealed record ClaudeCodeStatus(string? Path, bool LoggedIn, string? Account, string? Plan)
{
    public bool Installed => Path is not null;
}

/// <summary>
/// Um modelo que o Claude Code oferece a esta conta. Value é o que vai em --model (apelido como
/// "opus", ou o nome completo); Efforts, os níveis de esforço que ele aceita (vazio = não tem).
/// </summary>
public sealed record ClaudeCodeModel(string Value, string Label, string? Resolved, IReadOnlyList<string> Efforts);

/// <summary>
/// Uma conversa com o Claude Code oficial instalado na máquina, rodando sem janela (como a
/// extensão do VS Code faz). O login é o do próprio Claude Code: o RootDock não vê nem guarda a
/// credencial. As ferramentas embutidas dele (que agiriam neste computador) ficam desligadas;
/// ele só enxerga as do RootDock, que agem no servidor da sessão ativa.
/// </summary>
public sealed class ClaudeCodeSession(
    string claudePath, string workDirectory, string? model, AssistantToolRunner runner, IAssistantObserver observer, string? effort = null) : IDisposable
{
    private readonly McpToolServer _server = new(runner.RunAsync);
    private string? _sessionId;
    private readonly StringBuilder _errors = new();
    private bool _started;
    private AssistantMode _startedMode;
    private bool _wroteText;
    private Process? _process;
    private string? _files;

    /// <summary>Acima disto as instruções vão por arquivo (a linha de comando do Windows para em 32 mil caracteres).</summary>
    private const int MaxInlineSystemChars = 12000;

    public bool HasHistory => _started;

    /// <summary>
    /// Procura o claude.exe: PATH, instalador oficial (~\.local\bin) e npm global. A variável de
    /// ambiente ROOTDOCK_CLAUDE_PATH, se apontar para um arquivo que existe, vale antes de tudo.
    /// </summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("ROOTDOCK_CLAUDE_PATH") is { Length: > 0 } chosen)
        {
            candidates.Add(chosen.Trim().Trim('"'));
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory.Trim(), "claude.exe"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory.Trim(), "claude.cmd"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // entrada inválida no PATH
            }
        }

        return null;
    }

    /// <summary>Instalado? Logado? (pergunta ao próprio Claude Code: "claude auth status").</summary>
    public static async Task<ClaudeCodeStatus> GetStatusAsync(CancellationToken ct)
    {
        if (Find() is not { } path)
        {
            return new ClaudeCodeStatus(null, false, null, null);
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(StartInfo(path, Path.GetTempPath(), ["auth", "status"]))!;
            using var kill = limit.Token.Register(() => Kill(process));
            process.ErrorDataReceived += (_, _) => { }; // lê e descarta: stderr cheio travaria o processo
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var logged = root.TryGetProperty("loggedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new ClaudeCodeStatus(path, logged, Text(root, "email"), Text(root, "subscriptionType"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Versão que não responde em JSON (ou que não respondeu a tempo): o primeiro pedido dirá se falta login.
            return new ClaudeCodeStatus(path, true, null, null);
        }
    }

    /// <summary>Os apelidos que o Claude Code sempre aceita; usados quando ele não informa a lista.</summary>
    public static readonly IReadOnlyList<ClaudeCodeModel> FallbackModels =
    [
        new("opus", "Opus", null, []),
        new("sonnet", "Sonnet", null, []),
        new("haiku", "Haiku", null, []),
    ];

    /// <summary>
    /// Os modelos que a conta pode usar, perguntados ao próprio Claude Code (a mesma lista do
    /// seletor da extensão do VS Code). O primeiro item, de Value "default", é o padrão da conta.
    /// Lança ChatModelException se ele não responder.
    /// </summary>
    public static async Task<IReadOnlyList<ClaudeCodeModel>> ListModelsAsync(CancellationToken ct)
    {
        var path = Find() ?? throw new ChatModelException("O Claude Code não está instalado nesta máquina.");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            string[] arguments =
            [
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--tools", "", "--strict-mcp-config", "--setting-sources", "",
            ];
            using var process = Process.Start(StartInfo(path, Path.GetTempPath(), arguments))!;
            using var kill = limit.Token.Register(() => Kill(process));
            process.ErrorDataReceived += (_, _) => { }; // lê e descarta: stderr cheio travaria o processo
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync("""{"type":"control_request","request_id":"models","request":{"subtype":"initialize"}}""").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false) is { } line)
                {
                    if (ParseModels(line) is { Count: > 0 } models)
                    {
                        return models;
                    }
                }
            }
            finally
            {
                Kill(process); // só a lista interessa; nenhuma mensagem foi enviada ao modelo
            }

            throw new ChatModelException("O Claude Code não informou os modelos (versão antiga?). Atualize-o com \"claude update\".");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ChatModelException("O Claude Code não respondeu com a lista de modelos.", ex);
        }
    }

    /// <summary>Lê a lista de modelos de uma linha da saída (a resposta ao pedido "initialize"); null se a linha é outra coisa.</summary>
    internal static IReadOnlyList<ClaudeCodeModel>? ParseModels(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Text(root, "type") != "control_response" ||
                !root.TryGetProperty("response", out var outer) || outer.ValueKind != JsonValueKind.Object ||
                !outer.TryGetProperty("response", out var inner) || inner.ValueKind != JsonValueKind.Object ||
                !inner.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var models = new List<ClaudeCodeModel>();
            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "value") is not { Length: > 0 } value || models.Any(m => m.Value == value))
                {
                    continue;
                }

                var efforts = item.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array
                    ? levels.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList()
                    : [];
                models.Add(new ClaudeCodeModel(value, Text(item, "displayName") is { Length: > 0 } name ? name : value, Text(item, "resolvedModel"), efforts));
            }

            return models;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Envia a mensagem e acompanha a resposta até o fim. Cancelar encerra o processo; a conversa
    /// continua de onde parou na próxima mensagem (o Claude Code guarda o histórico dele).
    /// </summary>
    public Task SendAsync(string system, string userText, CancellationToken ct) => SendAsync(system, userText, [], ct);

    /// <summary>Texto puro, ou blocos (imagens e texto) quando a pessoa anexou imagens.</summary>
    private static object Content(string text, IReadOnlyList<ChatImage> images)
    {
        if (images.Count == 0)
        {
            return text;
        }

        var blocks = images.Select(i => (object)new { type = "image", source = new { type = "base64", media_type = i.MediaType, data = i.Base64 } }).ToList();
        blocks.Add(new { type = "text", text });
        return blocks;
    }

    public async Task SendAsync(string system, string userText, IReadOnlyList<ChatImage> images, CancellationToken ct)
    {
        _server.TurnToken = ct;
        if (_process is { HasExited: false } && _startedMode != runner.Mode)
        {
            Restart(); // o modo mudou no meio de uma resposta: as ferramentas de web oferecidas dependem dele
        }

        var process = _process is { HasExited: false } running ? running : Start(system);
        _process = process;
        using var kill = ct.Register(() => Kill(process));
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = Content(userText, images) },
                parent_tool_use_id = (string?)null,
            });
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            _started = true;
            _wroteText = false;
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } output)
            {
                if (Handle(output))
                {
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string error;
            lock (_errors)
            {
                error = _errors.ToString().Trim();
            }

            throw new ChatModelException(error.Length > 0
                ? "O Claude Code encerrou: " + (error.Length > 600 ? error[..600] + "..." : error) + StartHint()
                : "O Claude Code encerrou sem responder." + StartHint());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ChatModelException("A conversa com o Claude Code caiu: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Encerra o processo mantendo a conversa: a próxima mensagem o reinicia de onde parou, já
    /// com as instruções novas (ex.: a pessoa trocou o modo).
    /// </summary>
    public void Restart()
    {
        if (_process is { } process)
        {
            Kill(process);
            process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_process is { } process)
        {
            Kill(process);
            process.Dispose();
        }

        _server.Dispose();
        if (_files is not null)
        {
            foreach (var file in new[] { _files + ".mcp.json", _files + ".system.txt" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // fica para trás; não tem segredo dentro
                }
            }
        }
    }

    /// <summary>Uma linha do Claude Code. true = a resposta terminou.</summary>
    private bool Handle(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            switch (Text(root, "type"))
            {
                case "system" when Text(root, "subtype") == "init":
                    _sessionId = Text(root, "session_id") ?? _sessionId;
                    break;

                // O Claude Code resume a conversa sozinho quando ela enche a janela do modelo.
                case "system" when Text(root, "subtype") == "compact_boundary":
                    observer.Note("Conversa longa: resumi o que já foi feito para continuar sem perder o fio.");
                    break;

                // Só o texto da conversa principal (subagentes têm parent_tool_use_id).
                case "stream_event" when !root.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null:
                    if (root.TryGetProperty("event", out var streamEvent) && Text(streamEvent, "type") == "content_block_delta" &&
                        streamEvent.TryGetProperty("delta", out var delta) && Text(delta, "type") == "text_delta" &&
                        Text(delta, "text") is { Length: > 0 } piece)
                    {
                        observer.Text(piece);
                        _wroteText = true;
                    }
                    else if (_wroteText && root.TryGetProperty("event", out var blockEvent) && Text(blockEvent, "type") == "content_block_start" &&
                             blockEvent.TryGetProperty("content_block", out var block) && Text(block, "type") == "text")
                    {
                        observer.Text("\n\n"); // texto novo depois de uma ferramenta
                    }

                    break;

                // Busca na web e leitura de páginas são do próprio Claude Code: só aparecem na tela
                // (as ferramentas do RootDock passam pelo servidor MCP e já aparecem por lá).
                case "assistant" or "user" when root.TryGetProperty("message", out var webMessage) &&
                                               webMessage.TryGetProperty("content", out var webContent) && webContent.ValueKind == JsonValueKind.Array:
                    foreach (var webBlock in webContent.EnumerateArray())
                    {
                        ShowWebTool(webBlock);
                    }

                    break;

                case "result":
                    _sessionId = Text(root, "session_id") ?? _sessionId;
                    if (root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True)
                    {
                        var detail = Text(root, "result") ?? Text(root, "subtype") ?? "erro";
                        throw new ChatModelException(detail.Contains("login", StringComparison.OrdinalIgnoreCase) || detail.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
                            ? "O Claude Code pediu login. Abra \"Trocar de provedor ou chave...\" no botão do modelo, abaixo do campo de mensagem, e clique em Entrar.\n" + detail
                            : "O Claude Code devolveu um erro: " + detail);
                    }

                    return true;
            }
        }

        return false;
    }

    private readonly Dictionary<string, ToolCall> _webCalls = new(StringComparer.Ordinal);

    private void ShowWebTool(JsonElement block)
    {
        switch (Text(block, "type"))
        {
            case "tool_use" when Text(block, "id") is { } id && !_webCalls.ContainsKey(id) && block.TryGetProperty("input", out var input) &&
                                 Text(block, "name") switch { "WebSearch" => AssistantTools.WebSearch, "WebFetch" => AssistantTools.WebFetch, _ => null } is { } name:
                var call = new ToolCall(id, name, input.Clone());
                _webCalls[id] = call;
                observer.ToolStarted(call, AssistantTools.Summarize(name, call.Input));
                break;

            case "tool_result" when Text(block, "tool_use_id") is { } id && _webCalls.Remove(id, out var started):
                var text = "";
                if (block.TryGetProperty("content", out var content))
                {
                    text = content.ValueKind == JsonValueKind.String
                        ? content.GetString() ?? ""
                        : content.ValueKind == JsonValueKind.Array
                            ? string.Join('\n', content.EnumerateArray().Select(part => Text(part, "text")).Where(part => part is not null))
                            : "";
                }

                var failed = block.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                observer.ToolFinished(started, new ToolResult(id, text, failed));
                break;
        }
    }

    /// <summary>Se o Claude Code nunca chegou a responder nesta conversa, o problema é da instalação: diz como resolver.</summary>
    private string StartHint() => _started
        ? ""
        : $"\n\nO Claude Code usado foi: {claudePath}. Se o erro continuar, atualize-o (claude update) ou instale pelo instalador oficial " +
          "(no PowerShell: irm https://claude.ai/install.ps1 | iex) e abra o RootDock de novo.";

    private Process Start(string system)
    {
        Directory.CreateDirectory(workDirectory);
        // A busca é sempre a oficial do Claude Code. Abrir uma página também, menos no modo
        // "perguntar tudo": aí vale a do RootDock, que passa pela confirmação da pessoa.
        var ask = runner.Mode == AssistantMode.Ask;
        _startedMode = runner.Mode;
        _server.Hidden = name => name == AssistantTools.WebSearch || (name == AssistantTools.WebFetch && !ask);
        var web = ask ? "WebSearch" : "WebSearch,WebFetch";
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            // Nada deste computador: das ferramentas embutidas só as de web (as oficiais, da Anthropic);
            // sem MCPs, hooks ou memórias do usuário.
            "--tools", web,
            "--strict-mcp-config",
            "--setting-sources", "",
            // As ferramentas do RootDock rodam sem pergunta do Claude Code: quem pergunta é o RootDock.
            "--permission-mode", "dontAsk",
            "--allowedTools", $"mcp__{McpToolServer.ServerName}__*,{web}",
        };
        if (Launcher(claudePath).ViaShell || system.Length > MaxInlineSystemChars)
        {
            // Pelo cmd.exe, aspas e quebras de linha não chegam inteiras, e a linha de comando do
            // Windows tem limite de tamanho: a configuração e as instruções vão em arquivos (a
            // senha do MCP continua só na variável de ambiente).
            if (_files is null)
            {
                // Sobras de quando o RootDock foi fechado à força (não têm segredo, só ocupam espaço).
                foreach (var old in Directory.EnumerateFiles(workDirectory, "claude-*.*").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // em uso por outra janela
                    }
                }
            }

            _files ??= Path.Combine(workDirectory, "claude-" + Guid.NewGuid().ToString("N")[..10]);
            File.WriteAllText(_files + ".mcp.json", McpConfig(), new UTF8Encoding(false));
            File.WriteAllText(_files + ".system.txt", system, new UTF8Encoding(false));
            arguments.AddRange(["--mcp-config", _files + ".mcp.json", "--append-system-prompt-file", _files + ".system.txt"]);
        }
        else
        {
            arguments.AddRange(["--mcp-config", McpConfig(), "--append-system-prompt", system]);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.AddRange(["--model", model.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            arguments.AddRange(["--effort", effort.Trim()]);
        }

        if (_sessionId is not null && _started)
        {
            arguments.AddRange(["--resume", _sessionId]);
        }

        var info = StartInfo(claudePath, workDirectory, arguments);
        info.Environment["ROOTDOCK_MCP_TOKEN"] = _server.Token;
        info.Environment["MCP_TOOL_TIMEOUT"] = "86400000"; // a pessoa pode demorar para confirmar
        try
        {
            var process = Process.Start(info) ?? throw new ChatModelException("Não foi possível iniciar o Claude Code.");
            lock (_errors)
            {
                _errors.Clear();
            }

            // Lê o stderr o tempo todo (senão o processo trava com o buffer cheio); guarda só o fim.
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
            return process;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ChatModelException("Não foi possível iniciar o Claude Code: " + ex.Message, ex);
        }
    }

    /// <summary>Configuração do servidor MCP; a senha entra por variável de ambiente, não fica no texto.</summary>
    private string McpConfig() => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [McpToolServer.ServerName] = new
            {
                type = "http",
                url = _server.Url,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ${ROOTDOCK_MCP_TOKEN}" },
            },
        },
    });

    internal static ProcessStartInfo StartInfo(string path, string directory, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var launcher = Launcher(path);
        info.FileName = launcher.FileName;
        if (launcher.ViaShell)
        {
            // cmd /s /c "..." : com /s o cmd tira só a primeira e a última aspa, e o resto chega como está.
            var line = string.Join(' ', arguments.Prepend(path).Select(ShellQuote));
            info.Arguments = $"/d /s /c \"{line}\"";
            return info;
        }

        foreach (var argument in launcher.Prefix.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// Como iniciar o Claude Code a partir do caminho encontrado. Instalação por npm deixa um
    /// claude.cmd, e o cmd.exe corta a linha na primeira quebra e reinterpreta aspas e "&": sempre
    /// que dá, chama direto o programa que o .cmd chamaria (o executável nativo das versões
    /// atuais, ou o node com o cli.js das antigas). ViaShell = não deu, vai pelo cmd.
    /// </summary>
    internal static (string FileName, string[] Prefix, bool ViaShell) Launcher(string path)
    {
        if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return (path, [], false);
        }

        var folder = Path.GetDirectoryName(path) ?? "";
        var package = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code");
        foreach (var native in new[]
                 {
                     Path.Combine(package, "bin", "claude.exe"),
                     Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe"),
                     Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code-win32-arm64", "claude.exe"),
                 })
        {
            if (File.Exists(native))
            {
                return (native, [], false);
            }
        }

        var node = Path.Combine(folder, "node.exe");
        node = File.Exists(node) ? node : "node.exe";
        var script = Path.Combine(package, "cli.js");
        if (File.Exists(script))
        {
            return (node, [script], false);
        }

        // Outro gerenciador (pnpm, yarn, volta...) ou outro formato: o próprio .cmd diz o que chama.
        foreach (var target in ShimTargets(path))
        {
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(target).Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (target, [], false);
            }

            if (target.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
            {
                return (node, [target], false);
            }
        }

        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", [], true);
    }

    /// <summary>Os arquivos que um atalho .cmd referencia a partir da própria pasta (%~dp0 ou %dp0%), se existirem.</summary>
    private static IEnumerable<string> ShimTargets(string cmd)
    {
        string text;
        try
        {
            var file = new FileInfo(cmd);
            if (file.Length > 64 * 1024)
            {
                yield break;
            }

            text = File.ReadAllText(cmd);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var folder = Path.GetDirectoryName(cmd) ?? "";
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text, @"%(?:~dp0|dp0%)[\\/]*([^""%\r\n]+?\.(?:exe|js|mjs|cjs))(?=[""\s]|$)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(folder, match.Groups[1].Value.Replace('/', '\\')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(target))
            {
                yield return target;
            }
        }
    }

    /// <summary>Argumento para a linha do cmd: entre aspas quando tem algo que ele reinterpretaria solto.</summary>
    internal static string ShellQuote(string argument)
    {
        var clean = string.Concat(argument.ReplaceLineEndings(" ").Select(c => c is '"' ? '\'' : c is '%' ? ' ' : c));
        if (clean.EndsWith('\\'))
        {
            clean += "\\"; // barra final antes da aspa de fechamento viraria aspa escapada
        }

        return clean.Length == 0 || clean.Any(c => c is ' ' or '\t' or ',' or ';' or '=' or '(' or ')' or '*' or '&' or '|' or '<' or '>' or '^')
            ? "\"" + clean + "\""
            : clean;
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
