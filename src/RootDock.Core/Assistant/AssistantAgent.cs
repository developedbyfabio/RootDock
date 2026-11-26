namespace RootDock.Core.Assistant;

/// <summary>Quem acompanha a conversa (a tela): texto chegando, ferramentas e pedidos de confirmação.</summary>
public interface IAssistantObserver
{
    void Text(string delta);

    void ToolStarted(ToolCall call, string summary);

    void ToolFinished(ToolCall call, ToolResult result);

    /// <summary>Pergunta à pessoa se o pedido pode rodar. false = recusado.</summary>
    Task<bool> ConfirmAsync(ToolCall call, string summary, PermissionCheck check, CancellationToken ct);

    /// <summary>Aviso fora da resposta (ex.: a resposta foi cortada pelo limite de tamanho).</summary>
    void Note(string text);
}

/// <summary>
/// O laço da conversa para os provedores por chave de API: pede a resposta, executa as
/// ferramentas que ela pedir (com a permissão da pessoa quando preciso) e devolve os resultados,
/// até o modelo terminar. O histórico fica sempre válido, inclusive depois de uma interrupção.
/// </summary>
public sealed class AssistantAgent(IChatModel model, AssistantToolRunner runner, IAssistantObserver observer)
{
    /// <summary>Trava contra um modelo preso num ciclo de ferramentas.</summary>
    private const int MaxSteps = 200;

    private readonly List<ChatTurn> _history = [];

    /// <summary>
    /// Tamanho da conversa (em caracteres) a partir do qual o começo é resumido. Fica bem abaixo
    /// da janela dos modelos menores (uns 100 mil tokens), para sobrar espaço para a resposta.
    /// </summary>
    public int CompactAtChars { get; set; } = 320_000;

    public bool HasHistory => _history.Count > 0;

    public Task SendAsync(string system, string userText, CancellationToken ct) => SendAsync(system, userText, [], ct);

    public async Task SendAsync(string system, string userText, IReadOnlyList<ChatImage> images, CancellationToken ct)
    {
        _history.Add(ChatTurn.FromUser(userText, images));
        for (var step = 0; step < MaxSteps; step++)
        {
            ChatTurn reply;
            try
            {
                if (Size(_history) > CompactAtChars)
                {
                    await CompactAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    reply = await model.CompleteAsync(system, _history, AssistantTools.Definitions, observer.Text, ct).ConfigureAwait(false);
                }
                catch (ChatModelException ex) when (_history.Count > 2 && LooksLikeTooLong(ex))
                {
                    // O provedor disse que a conversa não cabe mais: resume e tenta de novo, uma vez.
                    await CompactAsync(ct).ConfigureAwait(false);
                    reply = await model.CompleteAsync(system, _history, AssistantTools.Definitions, observer.Text, ct).ConfigureAwait(false);
                }
            }
            catch when (step == 0 && _history.Count > 0 && _history[^1].Role == ChatRole.User && _history[^1].Text == userText)
            {
                // Nada foi respondido: a pergunta sai do histórico para a pessoa poder reenviar.
                _history.RemoveAt(_history.Count - 1);
                throw;
            }

            _history.Add(reply);
            if (reply.StopNote is { } note)
            {
                observer.Note(note);
            }

            if (reply.Calls.Count == 0)
            {
                return;
            }

            _history.Add(ChatTurn.FromResults(await RunToolsAsync(reply.Calls, ct).ConfigureAwait(false)));
            ct.ThrowIfCancellationRequested();
        }

        observer.Note($"Parei depois de {MaxSteps} passos seguidos. Diga \"continue\" se quiser que eu siga.");
    }

    /// <summary>
    /// Conversa longa demais para a janela do modelo: ele mesmo escreve um resumo do que foi dito
    /// e feito, e a conversa segue a partir do resumo (como o Claude Code faz). O que estava em
    /// andamento continua: o resumo leva o pedido atual e os últimos resultados.
    /// </summary>
    private async Task CompactAsync(CancellationToken ct)
    {
        var transcript = Transcript(_history, Math.Max(CompactAtChars, 320_000));
        const string Instructions =
            "Você resume conversas de trabalho entre uma pessoa e um assistente que administra um servidor e programa nele. " +
            "Escreva em português um resumo fiel e completo para o próprio assistente continuar o trabalho sem a conversa original: " +
            "1) o que a pessoa pediu e as preferências que ela deixou claras; 2) o que já foi feito, com caminhos de arquivos, " +
            "comandos importantes e os resultados que importam; 3) decisões tomadas e por quê; 4) erros encontrados e como foram " +
            "resolvidos; 5) o que está em andamento agora e os próximos passos. Seja concreto: nomes, caminhos e valores exatos. " +
            "Não invente nada que não esteja na conversa.";
        var summary = await model.CompleteAsync(Instructions, [ChatTurn.FromUser(transcript)], [], _ => { }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary.Text))
        {
            throw new ChatModelException("A conversa ficou longa demais e o modelo não conseguiu resumi-la. Comece uma nova conversa (a atual fica no histórico).");
        }

        _history.Clear();
        _history.Add(ChatTurn.FromUser(
            "[A conversa ficou longa e o começo foi substituído por este resumo, escrito por você mesmo.]\n" + summary.Text.Trim() +
            "\n[Fim do resumo. Continue o trabalho de onde parou, sem recomeçar e sem repetir o que já foi feito.]"));
        observer.Note("Conversa longa: resumi o que já foi feito para continuar sem perder o fio.");
    }

    private static int Size(IEnumerable<ChatTurn> history) =>
        history.Sum(t => t.Text.Length + t.Images.Count * 6000 +t.Calls.Sum(c => c.Input.GetRawText().Length + c.Name.Length) + t.Results.Sum(r => r.Content.Length));

    /// <summary>A conversa em texto para o resumo; resultados de ferramenta entram cortados e, se ainda não couber, fica o fim.</summary>
    private static string Transcript(IEnumerable<ChatTurn> history, int budget)
    {
        var text = new System.Text.StringBuilder();
        foreach (var turn in history)
        {
            switch (turn.Role)
            {
                case ChatRole.User:
                    text.Append("PESSOA: ").Append(turn.Text)
                        .Append(turn.Images.Count > 0 ? $" [{turn.Images.Count} imagem(ns) anexada(s), não incluída(s) neste resumo]" : "")
                        .Append("\n\n");
                    break;

                case ChatRole.Assistant:
                    if (turn.Text.Length > 0)
                    {
                        text.Append("ASSISTENTE: ").Append(turn.Text).Append('\n');
                    }

                    foreach (var call in turn.Calls)
                    {
                        var input = call.Input.GetRawText();
                        text.Append("FERRAMENTA ").Append(call.Name).Append(": ").Append(input.Length > 3000 ? input[..3000] + " [...]" : input).Append('\n');
                    }

                    text.Append('\n');
                    break;

                default:
                    foreach (var result in turn.Results)
                    {
                        text.Append("RESULTADO: ").Append(result.Content.Length > 3000 ? result.Content[..3000] + " [...]" : result.Content).Append("\n\n");
                    }

                    break;
            }
        }

        var all = text.ToString();
        return all.Length <= budget ? all : "[... começo omitido ...]\n" + all[^budget..];
    }

    private static bool LooksLikeTooLong(ChatModelException ex)
    {
        var message = ex.Message;
        return message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("maximum", StringComparison.OrdinalIgnoreCase) && message.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Um resultado por pedido, sempre: o provedor recusa a conversa se faltar algum.</summary>
    private async Task<IReadOnlyList<ToolResult>> RunToolsAsync(IReadOnlyList<ToolCall> calls, CancellationToken ct)
    {
        var results = new List<ToolResult>(calls.Count);
        foreach (var call in calls)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new ToolResult(call.Id, "Interrompido pela pessoa antes de executar.", IsError: true));
                continue;
            }

            ToolResult result;
            try
            {
                result = await runner.RunAsync(call, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult(call.Id, "Interrompido pela pessoa durante a execução.", IsError: true);
            }

            results.Add(result);
        }

        return results;
    }
}

/// <summary>
/// Executa um pedido de ferramenta com a regra de permissão: o que é só leitura roda direto, o
/// resto depende do modo, e o que é destrutivo sempre passa pela confirmação da pessoa. É o
/// mesmo caminho para os provedores por chave de API e para o Claude Code.
/// </summary>
public sealed class AssistantToolRunner(AssistantTools tools, IAssistantObserver observer)
{
    public AssistantMode Mode { get; set; } = AssistantMode.Ask;

    /// <summary>Os bloqueios da pessoa, lidos a cada pedido (ela pode mudar no meio da conversa).</summary>
    public Func<IReadOnlyList<string>>? Blocked { get; set; }

    public async Task<ToolResult> RunAsync(ToolCall call, CancellationToken ct)
    {
        var definition = AssistantTools.Find(call.Name);
        if (definition is null)
        {
            return new ToolResult(call.Id, $"Ferramenta desconhecida: {call.Name}.", IsError: true);
        }

        var summary = AssistantTools.Summarize(call.Name, call.Input);
        var check = AssistantPolicy.Evaluate(definition, call.Input, Mode);
        observer.ToolStarted(call, summary);
        ToolResult result;
        // Bloqueio da pessoa: vale em qualquer modo e não vira pergunta, é recusa.
        if (!AssistantTools.IsWeb(call.Name) && !AssistantTools.IsMacro(call.Name) &&
            Blocked?.Invoke() is { Count: > 0 } patterns && AssistantRules.Blocked(patterns, call.Input) is { } hit)
        {
            result = new ToolResult(call.Id, AssistantRules.BlockedMessage(hit), IsError: true);
        }
        else if (check.Kind != PermissionKind.Allow && !await observer.ConfirmAsync(call, summary, check, ct).ConfigureAwait(false))
        {
            result = new ToolResult(call.Id, "A pessoa não autorizou esta ação. Não tente de outro jeito: pergunte como ela prefere seguir.", IsError: true);
        }
        else
        {
            result = await tools.ExecuteAsync(call, ct).ConfigureAwait(false);
        }

        observer.ToolFinished(call, result);
        return result;
    }
}
