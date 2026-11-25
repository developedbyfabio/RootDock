using System.Globalization;
using System.Text;
using System.Text.Json;
using RootDock.Core.MultiExec;
using RootDock.Core.Sftp;
using RootDock.Core.Text;

namespace RootDock.Core.Assistant;

/// <summary>Um item de pasta, como o assistente o vê.</summary>
public sealed record AssistantFileEntry(string Name, bool IsDirectory, bool IsSymlink, long Size, DateTime Modified, string Permissions);

/// <summary>
/// O servidor em que o assistente age: a sessão SSH em foco. Os comandos rodam num canal à parte
/// da mesma conexão (saída limpa e código de retorno); o terminal visível é lido e digitado por
/// métodos próprios.
/// </summary>
public interface IAssistantHost
{
    /// <summary>Ex.: "root@10.0.0.7 (api)".</summary>
    string Label { get; }

    /// <summary>Pasta atual do terminal visível, quando conhecida.</summary>
    string? TerminalDirectory { get; }

    /// <param name="cwd">Pasta pedida pelo modelo; null = a do terminal.</param>
    Task<CommandResult> RunAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct);

    /// <summary>Consulta que não muda nada (busca em arquivos): sempre no canal próprio, fora do terminal visível.</summary>
    Task<CommandResult> QueryAsync(string command, TimeSpan timeout, CancellationToken ct);

    /// <summary>Guarda uma anotação permanente sobre este servidor.</summary>
    void Remember(string note);

    /// <summary>Últimas linhas do terminal visível, sem cores.</summary>
    string ReadTerminal(int lines);

    void TypeInTerminal(string text);

    Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken ct);

    Task WriteFileAsync(string path, byte[] content, CancellationToken ct);

    Task<bool> ExistsAsync(string path, CancellationToken ct);

    Task<IReadOnlyList<AssistantFileEntry>> ListAsync(string path, CancellationToken ct);

    /// <summary>Chamado antes de um arquivo ser gravado, com o conteúdo anterior (null = não existia), para a pessoa poder desfazer.</summary>
    void BeforeChange(string path, byte[]? previous)
    {
    }

    /// <summary>Mostra na tela a lista de tarefas do trabalho em andamento.</summary>
    void ShowTasks(IReadOnlyList<AssistantTask> tasks)
    {
    }
}

/// <summary>Um passo do plano do assistente. Status: "pending", "in_progress" ou "done".</summary>
public sealed record AssistantTask(string Title, string Status);

/// <summary>
/// As ferramentas do assistente (terminal e arquivos do servidor) e a execução de cada uma.
/// O texto devolvido vai para o modelo; erros voltam como texto para ele poder corrigir o rumo.
/// </summary>
public sealed class AssistantTools(IAssistantHost host, WebTools? web = null)
{
    public const string UpdateTasks = "update_tasks";
    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string RunCommand = "run_command";
    public const string ReadTerminalTool = "read_terminal";
    public const string TypeInTerminal = "type_in_terminal";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string EditFile = "edit_file";
    public const string ListDirectory = "list_directory";
    public const string SearchFiles = "search_files";
    public const string FindFiles = "find_files";
    public const string RememberTool = "remember";

    /// <summary>Pastas que a busca pula: histórico do git e dependências baixadas.</summary>
    private static readonly string[] SkippedDirectories = [".git", "node_modules", "vendor", ".cache"];

    /// <summary>Teto do que volta para o modelo por ferramenta (começo e fim; o meio é cortado).</summary>
    internal const int OutputHead = 24_000;
    internal const int OutputTail = 12_000;

    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int DefaultReadLines = 2000;
    private const int MaxLineChars = 2000;
    private const int MaxMatches = 200;
    /// <summary>Sem timeout_seconds o comando roda até terminar (a pessoa interrompe pelo botão de parar).</summary>
    private static readonly TimeSpan NoTimeout = TimeSpan.FromDays(7);

    private const int MaxTimeoutSeconds = 7 * 24 * 3600;

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new(RunCommand,
            "Executa um comando de shell no servidor desta conversa e devolve a saída e o código de retorno. " +
            "O comando roda até terminar, demore o que demorar (du, find, backup, build): espere o resultado, não repita nem " +
            "desista. Não há teclado: comandos que esperam resposta (senha, confirmação, editores) ou que nunca terminam " +
            "(top, tail -f, watch) ficariam presos; use opções não interativas (-y, --no-pager, -n), passe timeout_seconds " +
            "para os que não terminam sozinhos, ou use type_in_terminal.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "O comando, como seria digitado no shell." },
                    cwd = new { type = "string", description = "Pasta em que o comando roda (caminho absoluto). Padrão: a pasta atual do terminal." },
                    timeout_seconds = new { type = "integer", description = "Só para comandos que não terminam sozinhos: interrompe depois de tantos segundos. Sem ele, o comando roda até o fim." },
                },
                required = new[] { "command" },
            }),
            ReadOnly: false),
        new(ReadTerminalTool,
            "Lê as últimas linhas que estão no terminal visível da pessoa (o que ela digitou e o que o servidor respondeu). " +
            "Use para ver um erro que ela acabou de receber ou o resultado de type_in_terminal.",
            Schema(new
            {
                type = "object",
                properties = new { lines = new { type = "integer", description = "Quantas linhas do fim (padrão 80, máximo 500)." } },
            }),
            ReadOnly: true),
        new(TypeInTerminal,
            "Digita um texto no terminal visível da pessoa, como se ela tivesse digitado. Use só quando o comando precisa " +
            "do contexto daquele terminal (ela virou root com sudo su, está dentro de um container, de um mysql, de um " +
            "tmux) ou quando ela quer acompanhar a saída ao vivo. Não devolve a saída: leia depois com read_terminal.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "O texto a digitar." },
                    press_enter = new { type = "boolean", description = "Apertar Enter no fim (padrão true)." },
                },
                required = new[] { "text" },
            }),
            ReadOnly: false),
        new(ReadFile,
            "Lê um arquivo de texto do servidor (SFTP). Devolve as linhas numeradas. Para arquivos grandes, use offset e limit.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Caminho absoluto do arquivo." },
                    offset = new { type = "integer", description = "Primeira linha a ler (1 = começo)." },
                    limit = new { type = "integer", description = $"Quantas linhas (padrão {DefaultReadLines})." },
                },
                required = new[] { "path" },
            }),
            ReadOnly: true),
        new(WriteFile,
            "Cria um arquivo no servidor ou substitui todo o conteúdo de um existente (dono e permissões do existente são mantidos). " +
            "Para mudar parte de um arquivo, prefira edit_file.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Caminho absoluto do arquivo." },
                    content = new { type = "string", description = "O conteúdo completo." },
                },
                required = new[] { "path", "content" },
            }),
            ReadOnly: false),
        new(EditFile,
            "Troca um trecho exato de um arquivo de texto do servidor. old_text precisa aparecer uma única vez " +
            "(inclua linhas vizinhas para ficar único) ou use replace_all. Codificação e fim de linha do arquivo são mantidos.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Caminho absoluto do arquivo." },
                    old_text = new { type = "string", description = "O trecho atual, exatamente como está no arquivo." },
                    new_text = new { type = "string", description = "O trecho novo." },
                    replace_all = new { type = "boolean", description = "Trocar todas as ocorrências (padrão false)." },
                },
                required = new[] { "path", "old_text", "new_text" },
            }),
            ReadOnly: false),
        new(ListDirectory,
            "Lista uma pasta do servidor (SFTP): nome, tipo, tamanho, data e permissões.",
            Schema(new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Caminho absoluto da pasta." } },
                required = new[] { "path" },
            }),
            ReadOnly: true),
        new(SearchFiles,
            "Procura um texto ou expressão regular dentro dos arquivos de uma pasta do servidor, em todas as subpastas " +
            "(como grep -rn). Pula .git, node_modules, vendor e arquivos binários. Devolve arquivo:linha:trecho. " +
            "Use para achar onde algo é definido ou usado antes de editar.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Texto ou expressão regular estendida (ERE)." },
                    path = new { type = "string", description = "Pasta (ou arquivo) onde procurar, caminho absoluto." },
                    glob = new { type = "string", description = "Só arquivos com este nome, ex.: *.php, *.blade.php." },
                    ignore_case = new { type = "boolean", description = "Ignorar maiúsculas e minúsculas (padrão false)." },
                },
                required = new[] { "pattern", "path" },
            }),
            ReadOnly: true),
        new(FindFiles,
            "Encontra arquivos pelo nome dentro de uma pasta do servidor, em todas as subpastas. Aceita curingas (* e ?), " +
            "ex.: *.blade.php, *Controller.php, config/*.php. Pula .git, node_modules e vendor.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Nome do arquivo com curingas; com barra, casa com o fim do caminho." },
                    path = new { type = "string", description = "Pasta onde procurar, caminho absoluto." },
                },
                required = new[] { "pattern", "path" },
            }),
            ReadOnly: true),
        new(RememberTool,
            "Guarda uma anotação permanente sobre este servidor ou sobre um projeto nele: onde o projeto fica, como rodar e " +
            "publicar, convenções do código, preferências da pessoa, armadilhas já descobertas. As anotações entram em toda " +
            "conversa nova deste servidor. Use quando descobrir algo que vai valer no futuro ou quando ela pedir para lembrar. " +
            "Uma frase curta e concreta por anotação; nunca guarde senhas ou tokens.",
            Schema(new
            {
                type = "object",
                properties = new { note = new { type = "string", description = "A anotação, em uma frase." } },
                required = new[] { "note" },
            }),
            ReadOnly: true),
        new(UpdateTasks,
            "Mostra à pessoa a lista de tarefas do trabalho em andamento. Use em trabalhos com três passos ou mais (criar um " +
            "sistema, migrar algo, investigar um problema grande): monte a lista antes de começar e chame de novo a cada passo, " +
            "sempre com a lista inteira. Deixe só uma tarefa em in_progress por vez e marque done assim que terminar cada uma, " +
            "não tudo junto no fim. Não use para pedidos simples de um passo.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    tasks = new
                    {
                        type = "array",
                        description = "A lista inteira, na ordem.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string", description = "O passo, em poucas palavras." },
                                status = new { type = "string", @enum = new[] { "pending", "in_progress", "done" } },
                            },
                            required = new[] { "title", "status" },
                        },
                    },
                },
                required = new[] { "tasks" },
            }),
            ReadOnly: true),
        new(WebSearch,
            "Busca na internet (roda no computador da pessoa, não no servidor). Use para consultar documentação atual, a " +
            "mensagem de um erro, a versão de um pacote ou como algo se faz hoje, em vez de confiar na memória. Devolve título, " +
            "endereço e um trecho de cada resultado; abra o que interessa com web_fetch.",
            Schema(new
            {
                type = "object",
                properties = new { query = new { type = "string", description = "O que procurar, em poucas palavras (como se digita num buscador)." } },
                required = new[] { "query" },
            }),
            ReadOnly: true),
        new(WebFetch,
            "Abre uma página da internet e devolve o texto dela (títulos, listas, blocos de código e links). Só endereços " +
            "públicos http/https; páginas longas vêm em partes (use offset para continuar). Nunca coloque no endereço dados " +
            "do servidor (senhas, tokens, conteúdo de arquivos).",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "Endereço completo, com https://." },
                    offset = new { type = "integer", description = "Posição (em caracteres) de onde continuar uma página longa." },
                },
                required = new[] { "url" },
            }),
            ReadOnly: false),
    ];

    /// <summary>Ferramentas que agem na internet, e não no servidor da sessão.</summary>
    public static bool IsWeb(string name) => name is WebSearch or WebFetch;

    public static ToolDefinition? Find(string name) => Definitions.FirstOrDefault(d => d.Name == name);

    /// <summary>Resumo de uma linha do pedido, para a pessoa ver o que vai acontecer.</summary>
    public static string Summarize(string name, JsonElement input) => name switch
    {
        RunCommand => Text(input, "command") ?? "",
        TypeInTerminal => Text(input, "text") ?? "",
        ReadTerminalTool => "Ler o terminal",
        ReadFile or WriteFile or EditFile or ListDirectory => Text(input, "path") ?? "",
        SearchFiles or FindFiles => $"{Text(input, "pattern")}  em  {Text(input, "path")}",
        RememberTool => Text(input, "note") ?? "",
        WebSearch => Text(input, "query") ?? "",
        WebFetch => Text(input, "url") ?? "",
        UpdateTasks => "Lista de tarefas",
        _ => name,
    };

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        try
        {
            var text = call.Name switch
            {
                RunCommand => await RunCommandAsync(call.Input, ct).ConfigureAwait(false),
                ReadTerminalTool => ReadTerminal(call.Input),
                TypeInTerminal => Type(call.Input),
                ReadFile => await ReadFileAsync(call.Input, ct).ConfigureAwait(false),
                WriteFile => await WriteFileAsync(call.Input, ct).ConfigureAwait(false),
                EditFile => await EditFileAsync(call.Input, ct).ConfigureAwait(false),
                ListDirectory => await ListAsync(call.Input, ct).ConfigureAwait(false),
                SearchFiles => await SearchAsync(call.Input, ct).ConfigureAwait(false),
                FindFiles => await FindFilesAsync(call.Input, ct).ConfigureAwait(false),
                RememberTool => Remember(call.Input),
                UpdateTasks => Tasks(call.Input),
                WebSearch => await Web().SearchAsync(Required(call.Input, "query"), ct).ConfigureAwait(false),
                WebFetch => await Web().FetchAsync(Required(call.Input, "url"), Number(call.Input, "offset") ?? 0, ct).ConfigureAwait(false),
                _ => throw new ToolInputException($"Ferramenta desconhecida: {call.Name}."),
            };
            return new ToolResult(call.Id, text, IsError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(call.Id, "Erro: " + ex.Message, IsError: true);
        }
    }

    private async Task<string> RunCommandAsync(JsonElement input, CancellationToken ct)
    {
        var command = Required(input, "command");
        var cwd = Text(input, "cwd") is { Length: > 0 } given ? given : null;
        var seconds = Number(input, "timeout_seconds") is { } asked ? Math.Clamp(asked, 1, MaxTimeoutSeconds) : (int?)null;
        CommandResult result;
        try
        {
            result = await host.RunAsync(command, cwd, seconds is { } limit ? TimeSpan.FromSeconds(limit) : NoTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ToolInputException($"O comando não terminou em {seconds} s (o timeout_seconds pedido) e foi interrompido. Se ele só demora, rode de novo sem timeout_seconds: ele vai até o fim.");
        }

        var text = new StringBuilder();
        text.Append(Clip(result.Output));
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            if (text.Length > 0 && text[^1] != '\n')
            {
                text.Append('\n');
            }

            text.Append("[stderr]\n").Append(Clip(result.Error));
        }

        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }

        text.Append(CultureInfo.InvariantCulture, $"[código de retorno: {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "desconhecido"}]");
        return text.ToString();
    }

    private string ReadTerminal(JsonElement input)
    {
        var lines = Math.Clamp(Number(input, "lines") ?? 80, 1, 500);
        var text = host.ReadTerminal(lines);
        return text.Length == 0 ? "(terminal vazio)" : text;
    }

    private string Type(JsonElement input)
    {
        var text = Required(input, "text");
        var enter = !input.TryGetProperty("press_enter", out var flag) || flag.ValueKind != JsonValueKind.False;
        host.TypeInTerminal(text.Replace("\r\n", "\n").Replace('\n', '\r') + (enter ? "\r" : ""));
        return "Digitado no terminal. Use read_terminal para ver o resultado.";
    }

    private async Task<string> SearchAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        var pattern = Required(input, "pattern");
        var ignoreCase = input.TryGetProperty("ignore_case", out var flag) && flag.ValueKind == JsonValueKind.True;
        var include = Text(input, "glob") is { Length: > 0 } glob ? " --include=" + RemotePath.ShellQuote(glob) : "";
        var skip = string.Concat(SkippedDirectories.Select(d => " --exclude-dir=" + d));
        var command = $"grep -rnI{(ignoreCase ? "i" : "")} -E{skip}{include} -e {RemotePath.ShellQuote(pattern)} -- {RemotePath.ShellQuote(path)} 2>/dev/null | cut -c1-400 | head -n {MaxMatches + 1}";
        var result = await host.QueryAsync(command, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        return Listing(result.Output, "Nenhuma ocorrência.", "ocorrências; refine o padrão, a pasta ou o glob para ver o resto");
    }

    private async Task<string> FindFilesAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        var pattern = Required(input, "pattern");
        var test = pattern.Contains('/', StringComparison.Ordinal) ? "-path " + RemotePath.ShellQuote("*" + pattern.TrimStart('/')) : "-name " + RemotePath.ShellQuote(pattern);
        var skip = string.Join(" -o ", SkippedDirectories.Select(d => "-name " + d));
        var command = $"find {RemotePath.ShellQuote(path)} \\( {skip} \\) -prune -o -type f {test} -print 2>/dev/null | head -n {MaxMatches + 1}";
        var result = await host.QueryAsync(command, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        return Listing(result.Output, "Nenhum arquivo com esse nome.", "arquivos; refine o nome ou a pasta para ver o resto");
    }

    private static string Listing(string output, string empty, string more)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return empty;
        }

        var text = string.Join('\n', lines.Take(MaxMatches));
        return lines.Length > MaxMatches ? $"{text}\n[mostradas as primeiras {MaxMatches} {more}]" : text;
    }

    private WebTools Web() => web ?? throw new ToolInputException("A busca na web não está disponível aqui.");

    private string Tasks(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("tasks", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException("Falta o parâmetro tasks (a lista inteira).");
        }

        var tasks = new List<AssistantTask>();
        foreach (var item in list.EnumerateArray())
        {
            if (Text(item, "title") is { Length: > 0 } title)
            {
                var status = Text(item, "status") switch
                {
                    "done" or "completed" => "done",
                    "in_progress" => "in_progress",
                    _ => "pending",
                };
                tasks.Add(new AssistantTask(title.ReplaceLineEndings(" ").Trim(), status));
            }
        }

        host.ShowTasks(tasks);
        var done = tasks.Count(t => t.Status == "done");
        return tasks.Count == 0 ? "Lista de tarefas limpa." : $"Lista atualizada: {done} de {tasks.Count} concluídas.";
    }

    private string Remember(JsonElement input)
    {
        var note = Required(input, "note");
        host.Remember(note);
        return "Anotado. Vai entrar em toda conversa nova deste servidor.";
    }

    /// <summary>Arquivo grande demais para ler inteiro: lê só as linhas pedidas, pelo próprio servidor.</summary>
    private async Task<string> ReadLargeFileAsync(string path, JsonElement input, CancellationToken ct)
    {
        var offset = Math.Max(1, Number(input, "offset") ?? 1);
        var limit = Math.Clamp(Number(input, "limit") ?? 400, 1, 2000);
        var end = offset + limit - 1;
        var command = $"awk 'NR>={offset} && NR<={end} {{ printf \"%d\\t%s\\n\", NR, substr($0, 1, {MaxLineChars}) }} NR>{end} {{ exit }}' {RemotePath.ShellQuote(path)}";
        var result = await host.QueryAsync(command, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.Output.Length == 0)
        {
            throw new ToolInputException("Não foi possível ler o arquivo: " + result.Error.Trim());
        }

        return result.Output.Length == 0
            ? $"O arquivo não tem a linha {offset}."
            : Clip(result.Output) + $"[arquivo grande: mostradas as linhas {offset} a {end}; continue com offset {end + 1}, ou use search_files para ir direto ao trecho]";
    }

    private async Task<string> ReadFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        byte[] bytes;
        try
        {
            bytes = await host.ReadFileAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.Message.Contains("grande demais", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadLargeFileAsync(path, input, ct).ConfigureAwait(false);
        }

        if (TextFile.LooksBinary(bytes))
        {
            return $"Arquivo binário ({bytes.Length:N0} bytes): não dá para ler como texto.";
        }

        var lines = TextFile.Decode(bytes).Text.Replace("\r\n", "\n").Split('\n');
        var offset = Math.Max(1, Number(input, "offset") ?? 1);
        var limit = Math.Clamp(Number(input, "limit") ?? DefaultReadLines, 1, 20_000);
        if (lines.Length == 1 && lines[0].Length == 0)
        {
            return "(arquivo vazio)";
        }

        if (offset > lines.Length)
        {
            return $"O arquivo tem {lines.Length} linhas; offset {offset} passa do fim.";
        }

        var text = new StringBuilder();
        var end = Math.Min(lines.Length, offset - 1 + limit);
        for (var i = offset - 1; i < end; i++)
        {
            var line = lines[i].Length > MaxLineChars ? lines[i][..MaxLineChars] + " [linha cortada]" : lines[i];
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}\t{line}\n");
            if (text.Length > OutputHead + OutputTail)
            {
                text.Append(CultureInfo.InvariantCulture, $"[cortado na linha {i + 1} de {lines.Length}: continue com offset {i + 2}]");
                return text.ToString();
            }
        }

        if (end < lines.Length)
        {
            text.Append(CultureInfo.InvariantCulture, $"[mostradas as linhas {offset} a {end} de {lines.Length}: continue com offset {end + 1}]");
        }

        return text.ToString();
    }

    private async Task<string> WriteFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        var content = Required(input, "content", allowEmpty: true);
        var existed = await host.ExistsAsync(path, ct).ConfigureAwait(false);
        byte[] bytes;
        if (existed)
        {
            // Mantém a codificação e o fim de linha do arquivo que já existe.
            var previous = await host.ReadFileAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
            host.BeforeChange(path, previous);
            var old = TextFile.Decode(previous);
            bytes =TextFile.Encode(WithLineEnding(content, old.LineEnding), old.EncodingName, old.HasBom);
        }
        else
        {
            bytes = TextFile.Encode(content.Replace("\r\n", "\n"), TextFile.Utf8, bom: false);
            host.BeforeChange(path, null);
        }

        await host.WriteFileAsync(path, bytes, ct).ConfigureAwait(false);
        return existed ? $"Arquivo substituído: {path} ({bytes.Length:N0} bytes)." : $"Arquivo criado: {path} ({bytes.Length:N0} bytes).";
    }

    private async Task<string> EditFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        var oldText = Required(input, "old_text").Replace("\r\n", "\n");
        var newText = Required(input, "new_text", allowEmpty: true).Replace("\r\n", "\n");
        var all = input.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (oldText == newText)
        {
            throw new ToolInputException("old_text e new_text são iguais: nada a trocar.");
        }

        var bytes = await host.ReadFileAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
        if (TextFile.LooksBinary(bytes))
        {
            throw new ToolInputException("Arquivo binário: não dá para editar como texto.");
        }

        var decoded = TextFile.Decode(bytes);
        var text = decoded.Text.Replace("\r\n", "\n");
        var count = Count(text, oldText);
        if (count == 0)
        {
            throw new ToolInputException("old_text não foi encontrado no arquivo. Leia o arquivo de novo e copie o trecho exatamente (espaços e tabs incluídos).");
        }

        if (count > 1 && !all)
        {
            throw new ToolInputException($"old_text aparece {count} vezes. Inclua linhas vizinhas para ficar único ou use replace_all.");
        }

        host.BeforeChange(path, bytes);
        var updated = text.Replace(oldText, newText, StringComparison.Ordinal);
        await host.WriteFileAsync(path, TextFile.Encode(WithLineEnding(updated, decoded.LineEnding), decoded.EncodingName, decoded.HasBom), ct).ConfigureAwait(false);
        return count == 1 ? $"Trecho trocado em {path}." : $"{count} ocorrências trocadas em {path}.";
    }

    private async Task<string> ListAsync(JsonElement input, CancellationToken ct)
    {
        var path = AbsolutePath(input);
        var entries = await host.ListAsync(path, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return "(pasta vazia)";
        }

        var text = new StringBuilder();
        foreach (var entry in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.Ordinal).Take(1000))
        {
            var kind = entry.IsSymlink ? "link" : entry.IsDirectory ? "pasta" : "arquivo";
            text.Append(CultureInfo.InvariantCulture, $"{entry.Permissions}\t{kind}\t{entry.Size}\t{entry.Modified:yyyy-MM-dd HH:mm}\t{entry.Name}\n");
        }

        if (entries.Count > 1000)
        {
            text.Append(CultureInfo.InvariantCulture, $"[mostrados 1000 de {entries.Count} itens]");
        }

        return text.ToString();
    }

    /// <summary>O comando como vai para o canal próprio da conexão: começa na pasta pedida.</summary>
    public static string ForChannel(string command, string? cwd) =>
        string.IsNullOrEmpty(cwd) ? command : $"cd {RemotePath.ShellQuote(cwd)} && {{ {command}\n}}";

    /// <summary>Corta o meio de um texto longo, deixando o começo e o fim.</summary>
    internal static string Clip(string text)
    {
        if (text.Length <= OutputHead + OutputTail)
        {
            return text;
        }

        var cut = text.Length - OutputHead - OutputTail;
        return string.Concat(text.AsSpan(0, OutputHead), $"\n[... {cut:N0} caracteres omitidos ...]\n", text.AsSpan(text.Length - OutputTail));
    }

    private static string WithLineEnding(string text, string lineEnding)
    {
        var unix = text.Replace("\r\n", "\n");
        return lineEnding == "CRLF" ? unix.Replace("\n", "\r\n") : unix;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string AbsolutePath(JsonElement input)
    {
        var path = Required(input, "path");
        return path.StartsWith('/') ? path : throw new ToolInputException("path precisa ser um caminho absoluto (começando com /).");
    }

    private static string Required(JsonElement input, string name, bool allowEmpty = false)
    {
        var value = Text(input, name);
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new ToolInputException($"Falta o parâmetro {name}.");
        }

        return value;
    }

    private static string? Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    private sealed class ToolInputException(string message) : Exception(message);
}
