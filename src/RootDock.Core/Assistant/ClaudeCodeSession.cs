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
/// Uma conversa com o Claude Code oficial instalado na máquina, rodando sem janela (como a
/// extensão do VS Code faz). O login é o do próprio Claude Code: o RootDock não vê nem guarda a
/// credencial. As ferramentas embutidas dele (que agiriam neste computador) ficam desligadas;
/// ele só enxerga as do RootDock, que agem no servidor da sessão ativa.
/// </summary>
public sealed class ClaudeCodeSession(
    string claudePath, string workDirectory, string? model, AssistantToolRunner runner, IAssistantObserver observer) : IDisposable
{
    private readonly McpToolServer _server = new(runner.RunAsync);
    private string? _sessionId;
    private readonly StringBuilder _errors = new();
    private bool _started;
    private AssistantMode _startedMode;
    private bool _wroteText;
    private Process? _process;

    public bool HasHistory => _started;

    /// <summary>Procura o claude.exe: PATH, instalador oficial (~\.local\bin) e npm global.</summary>
    public static string? Find()
    {
        var candidates = new List<string>();
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
                ? "O Claude Code encerrou: " + (error.Length > 600 ? error[..600] + "..." : error)
                : "O Claude Code encerrou sem responder.");
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
                            ? "O Claude Code pediu login. Abra as configurações do assistente e clique em Entrar.\n" + detail
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
            "--mcp-config", McpConfig(),
            // As ferramentas do RootDock rodam sem pergunta do Claude Code: quem pergunta é o RootDock.
            "--permission-mode", "dontAsk",
            "--allowedTools", $"mcp__{McpToolServer.ServerName}__*,{web}",
            "--append-system-prompt", system,
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.AddRange(["--model", model.Trim()]);
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

    private static ProcessStartInfo StartInfo(string path, string directory, IEnumerable<string> arguments)
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
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Instalação por npm. O cmd.exe corta a linha de comando na primeira quebra de linha e
            // reinterpreta aspas e "&", o que estragaria as instruções: chama o node direto no
            // programa que o claude.cmd chamaria.
            var folder = System.IO.Path.GetDirectoryName(path) ?? "";
            var script = System.IO.Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            var node = System.IO.Path.Combine(folder, "node.exe");
            if (File.Exists(script))
            {
                info.FileName = File.Exists(node) ? node : "node.exe";
                info.ArgumentList.Add(script);
            }
            else
            {
                // Formato de instalação desconhecido: vai pelo cmd, com os argumentos numa forma que ele não corrompe.
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add(path);
                arguments = arguments.Select(a => string.Concat(a.ReplaceLineEndings(" ").Select(c => c is '"' ? '\'' : c is '&' or '|' or '<' or '>' or '^' or '%' ? ' ' : c)));
            }
        }
        else
        {
            info.FileName = path;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
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
