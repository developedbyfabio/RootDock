using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace RootDock.Core.Assistant;

/// <summary>
/// Claude pela API da Anthropic (chave de API), com o SDK oficial. A resposta chega aos poucos;
/// os blocos de raciocínio voltam para a API exatamente como vieram (são assinados).
/// </summary>
public sealed class AnthropicChatModel(string apiKey, string model) : IChatModel
{
    /// <summary>Modelo usado quando a pessoa não escolhe outro.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    private const int MaxTokens = 32_000;

    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public static async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (Exception ex) when (Explain(ex) is { } explained)
        {
            throw explained;
        }
    }

    public async Task<ChatTurn> CompleteAsync(
        string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolDefinition> tools, Action<string> onText, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = system,
            Messages = history.Select(ToMessage).ToList(),
            Tools = tools.Select(ToTool).ToList(),
        };

        var blocks = new SortedDictionary<long, Block>();
        string? stop = null;
        try
        {
            await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, cancellationToken: ct).ConfigureAwait(false))
            {
                if (streamEvent.TryPickContentBlockStart(out var start))
                {
                    var block = new Block();
                    if (start.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        block.Kind = BlockKind.ToolUse;
                        block.Id = toolUse.ID;
                        block.Name = toolUse.Name;
                    }
                    else if (start.ContentBlock.TryPickThinking(out var thinking))
                    {
                        block.Kind = BlockKind.Thinking;
                        block.Text.Append(thinking.Thinking);
                        block.Signature = thinking.Signature;
                    }
                    else if (start.ContentBlock.TryPickRedactedThinking(out var redacted))
                    {
                        block.Kind = BlockKind.Redacted;
                        block.Data = redacted.Data;
                    }
                    else if (start.ContentBlock.TryPickText(out var text))
                    {
                        block.Kind = BlockKind.Text;
                        block.Text.Append(text.Text);
                    }
                    else
                    {
                        block.Kind = BlockKind.Other;
                    }

                    blocks[start.Index] = block;
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta) && blocks.TryGetValue(delta.Index, out var block))
                {
                    if (delta.Delta.TryPickText(out var text))
                    {
                        block.Text.Append(text.Text);
                        onText(text.Text);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json))
                    {
                        block.Text.Append(json.PartialJson);
                    }
                    else if (delta.Delta.TryPickThinking(out var thinking))
                    {
                        block.Text.Append(thinking.Thinking);
                    }
                    else if (delta.Delta.TryPickSignature(out var signature))
                    {
                        block.Signature = signature.Signature;
                    }
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stop = messageDelta.Delta.StopReason?.Raw() ?? stop;
                }
            }
        }
        catch (Exception ex) when (Explain(ex) is { } explained)
        {
            throw explained;
        }

        return ToTurn(blocks.Values, stop);
    }

    private static ChatTurn ToTurn(IEnumerable<Block> blocks, string? stop)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        var native = new List<ContentBlockParam>();
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Text when block.Text.Length > 0:
                    text.Append(block.Text);
                    native.Add(new TextBlockParam { Text = block.Text.ToString() });
                    break;

                case BlockKind.Thinking:
                    native.Add(new ThinkingBlockParam { Thinking = block.Text.ToString(), Signature = block.Signature ?? "" });
                    break;

                case BlockKind.Redacted:
                    native.Add(new RedactedThinkingBlockParam { Data = block.Data ?? "" });
                    break;

                case BlockKind.ToolUse:
                    // Só roda ferramenta com entrada completa: resposta cortada (max_tokens) ou recusada não executa nada.
                    if (stop is "max_tokens" or "refusal")
                    {
                        break;
                    }

                    var input = ParseInput(block.Text.ToString());
                    calls.Add(new ToolCall(block.Id ?? "", block.Name ?? "", JsonSerializer.SerializeToElement(input)));
                    native.Add(new ToolUseBlockParam { ID = block.Id ?? "", Name = block.Name ?? "", Input = input });
                    break;
            }
        }

        return new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = text.ToString(),
            Calls = calls,
            Native = native,
            StopNote = stop switch
            {
                "max_tokens" => "A resposta foi cortada pelo limite de tamanho. Diga \"continue\" para eu seguir.",
                "refusal" => "O Claude recusou este pedido (filtro de segurança da Anthropic). Reformule ou troque de modelo nas configurações.",
                _ => null,
            },
        };
    }

    /// <summary>Entrada da ferramenta como objeto; JSON inválido vira objeto vazio (a ferramenta acusa o que falta).</summary>
    private static Dictionary<string, JsonElement> ParseInput(string json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) is { } parsed)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // segue com o objeto vazio
            }
        }

        return [];
    }

    private static MessageParam ToMessage(ChatTurn turn)
    {
        switch (turn.Role)
        {
            case ChatRole.User:
                if (turn.Images.Count == 0)
                {
                    return new UserMessageParam { Content = turn.Text };
                }

                var parts = turn.Images.Select(i => (ContentBlockParam)new ImageBlockParam
                {
                    Source = new Base64ImageSource { Data = i.Base64, MediaType = i.MediaType },
                }).ToList();
                parts.Add(new TextBlockParam { Text = turn.Text });
                return new UserMessageParam { Content = parts };

            case ChatRole.Assistant:
                var content = turn.Native as List<ContentBlockParam> ?? Rebuild(turn);
                if (content.Count == 0)
                {
                    // Resposta vazia (só raciocínio descartado): a API não aceita conteúdo vazio.
                    content = [new TextBlockParam { Text = "(sem texto)" }];
                }

                return new AssistantMessageParam { Content = content };

            default:
                return new UserMessageParam
                {
                    Content = turn.Results.Select(r => (ContentBlockParam)new ToolResultBlockParam
                    {
                        ToolUseID = r.CallId,
                        Content = r.Content.Length > 0 ? r.Content : "(sem saída)",
                        IsError = r.IsError,
                    }).ToList(),
                };
        }
    }

    /// <summary>Resposta vinda de outro provedor (a pessoa trocou no meio da conversa).</summary>
    private static List<ContentBlockParam> Rebuild(ChatTurn turn)
    {
        var content = new List<ContentBlockParam>();
        if (turn.Text.Length > 0)
        {
            content.Add(new TextBlockParam { Text = turn.Text });
        }

        foreach (var call in turn.Calls)
        {
            content.Add(new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = ParseInput(call.Input.GetRawText()) });
        }

        return content;
    }

    private static ToolUnion ToTool(ToolDefinition definition)
    {
        var schema = definition.Schema;
        var properties = schema.TryGetProperty("properties", out var declared)
            ? declared.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : [];
        var required = schema.TryGetProperty("required", out var list)
            ? list.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];
        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = new() { Properties = properties, Required = required },
            // Conteúdo de arquivo chega conforme é gerado; a entrada é conferida pela ferramenta antes de rodar.
            EagerInputStreaming = true,
        };
    }

    /// <summary>Erro do SDK em português, ou null para deixar passar (cancelamento, erro de programação).</summary>
    private static ChatModelException? Explain(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException or AnthropicForbiddenException =>
            new ChatModelException("Chave de API da Anthropic recusada. Confira a chave nas configurações do assistente.", ex),
        AnthropicNotFoundException =>
            new ChatModelException("Modelo não encontrado na Anthropic. Confira o modelo nas configurações do assistente.", ex),
        AnthropicRateLimitException =>
            new ChatModelException("Limite de uso da Anthropic atingido. Espere um pouco e tente de novo.", ex),
        Anthropic5xxException =>
            new ChatModelException("A Anthropic está com problemas agora. Tente de novo em instantes.", ex),
        AnthropicBadRequestException =>
            new ChatModelException("A Anthropic recusou o pedido: " + ex.Message, ex),
        AnthropicIOException =>
            new ChatModelException("Não foi possível falar com a Anthropic (rede): " + ex.Message, ex),
        AnthropicApiException =>
            new ChatModelException("Erro da Anthropic: " + ex.Message, ex),
        HttpRequestException =>
            new ChatModelException("Não foi possível falar com a Anthropic (rede): " + ex.Message, ex),
        _ => null,
    };

    private enum BlockKind
    {
        Text,
        Thinking,
        Redacted,
        ToolUse,
        Other,
    }

    private sealed class Block
    {
        public BlockKind Kind { get; set; }

        public StringBuilder Text { get; } = new();

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Signature { get; set; }

        public string? Data { get; set; }
    }
}
