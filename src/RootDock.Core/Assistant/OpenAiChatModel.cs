using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RootDock.Core.Assistant;

/// <summary>
/// Provedores que falam o formato "chat completions" (OpenAI, Gemini pelo endereço compatível,
/// Groq, OpenRouter, Ollama, LM Studio...). As chamadas de ferramenta voltam para o provedor
/// exatamente como ele as mandou, com os campos extras que ele exigir de volta.
/// </summary>
public sealed class OpenAiChatModel(HttpClient http, string baseUrl, string apiKey, string model) : IChatModel
{
    private readonly string _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

    /// <summary>Modelos que a chave enxerga (para a lista da tela). Vazio se o provedor não listar.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        Authorize(request, apiKey);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, body);
        }

        var ids = new List<string>();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } text)
                {
                    // O Gemini lista como "models/gemini-..."; nas chamadas vale o nome sem o prefixo.
                    ids.Add(text.StartsWith("models/", StringComparison.Ordinal) ? text["models/".Length..] : text);
                }
            }
        }

        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    public async Task<ChatTurn> CompleteAsync(
        string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolDefinition> tools, Action<string> onText, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = Messages(system, history),
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Schema.GetRawText()),
                },
            }).ToArray());
        }

        var json = body.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            Authorize(request, apiKey);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ChatModelException($"Não foi possível falar com {new Uri(_endpoint).Host}: {ex.Message}", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await ReadStreamAsync(response, onText, ct).ConfigureAwait(false);
                }

                // Sobrecarga ou limite momentâneo (comum no plano gratuito do Gemini): espera e tenta de novo.
                if (attempt < RetryDelays.Length && (int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                {
                    var wait = response.Headers.RetryAfter?.Delta is { } asked && asked <= TimeSpan.FromSeconds(40) ? asked : RetryDelays[attempt];
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
        }
    }

    /// <summary>Esperas entre as tentativas quando o provedor está sobrecarregado (4 tentativas ao todo).</summary>
    internal static TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15)];

    private static async Task<ChatTurn> ReadStreamAsync(HttpResponseMessage response, Action<string> onText, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        string? finish = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line.AsSpan(5).Trim().ToString();
            if (data is "[DONE]" or "")
            {
                continue;
            }

            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk?["error"] is { } error)
            {
                throw new ChatModelException("O provedor devolveu um erro: " + (error["message"]?.ToString() ?? error.ToJsonString()));
            }

            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not { } choice)
            {
                continue;
            }

            finish = choice["finish_reason"]?.GetValue<string>() ?? finish;
            if (choice["delta"] is not JsonObject delta)
            {
                continue;
            }

            if (delta["content"] is JsonValue content && content.TryGetValue<string>(out var piece) && piece.Length > 0)
            {
                text.Append(piece);
                onText(piece);
            }

            if (delta["tool_calls"] is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    var index = part["index"] is JsonValue position && position.TryGetValue<int>(out var number) ? number : calls.Count == 0 ? 0 : calls.Keys.Max();
                    if (!calls.TryGetValue(index, out var call))
                    {
                        calls[index] = call = [];
                    }

                    Merge(call, part);
                }
            }
        }

        var toolCalls = new List<ToolCall>();
        var raw = new JsonArray();
        foreach (var call in calls.Values)
        {
            call.Remove("index");
            call["type"] ??= "function";
            var function = call["function"] as JsonObject ?? [];
            var name = function["name"]?.GetValue<string>() ?? "";
            var arguments = function["arguments"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(arguments))
            {
                arguments = "{}";
                function["arguments"] = arguments;
            }

            if (call["id"]?.GetValue<string>() is not { Length: > 0 } id)
            {
                call["id"] = id = "call_" + Guid.NewGuid().ToString("N")[..12];
            }

            JsonElement input;
            try
            {
                using var parsed = JsonDocument.Parse(arguments);
                input = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Argumentos cortados ou inválidos: a ferramenta recusa e o modelo tenta de novo.
                input = JsonSerializer.SerializeToElement(new { });
            }

            toolCalls.Add(new ToolCall(id, name, input));
            raw.Add(call.DeepClone());
        }

        return new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = text.ToString(),
            Calls = toolCalls,
            Native = raw,
            StopNote = finish switch
            {
                "length" => "A resposta foi cortada pelo limite de tamanho do modelo.",
                "content_filter" => "O provedor bloqueou a resposta (filtro de conteúdo).",
                _ => null,
            },
        };
    }

    /// <summary>Junta um pedaço de chamada de ferramenta ao que já chegou (textos se somam, o resto substitui).</summary>
    private static void Merge(JsonObject target, JsonObject part)
    {
        foreach (var (key, value) in part)
        {
            if (value is null)
            {
                continue;
            }

            if (value is JsonObject child)
            {
                if (target[key] is not JsonObject existing)
                {
                    target[key] = existing = [];
                }

                Merge(existing, child);
            }
            else if (key == "arguments" && value is JsonValue added && added.TryGetValue<string>(out var more) &&
                     target[key] is JsonValue old && old.TryGetValue<string>(out var before))
            {
                target[key] = before + more;
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static JsonArray Messages(string system, IReadOnlyList<ChatTurn> history)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var turn in history)
        {
            switch (turn.Role)
            {
                case ChatRole.User:
                    if (turn.Images.Count == 0)
                    {
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn.Text });
                        break;
                    }

                    var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = turn.Text } };
                    foreach (var image in turn.Images)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{image.Base64}" },
                        });
                    }

                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    break;

                case ChatRole.Assistant:
                    var message = new JsonObject { ["role"] = "assistant", ["content"] = turn.Text.Length > 0 ? turn.Text : null };
                    if (turn.Calls.Count > 0)
                    {
                        message["tool_calls"] = turn.Native is JsonArray raw
                            ? raw.DeepClone()
                            : new JsonArray(turn.Calls.Select(c => (JsonNode)new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Input.GetRawText() },
                            }).ToArray());
                    }

                    messages.Add(message);
                    break;

                case ChatRole.Tool:
                    foreach (var result in turn.Results)
                    {
                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = result.Content });
                    }

                    break;
            }
        }

        return messages;
    }

    private static void Authorize(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    private static ChatModelException Failure(HttpStatusCode status, string body)
    {
        var detail = body;
        try
        {
            // {"error":{"message":"..."}} ou [{"error":{...}}] (Gemini)
            var node = JsonNode.Parse(body);
            if (node is JsonArray { Count: > 0 } list)
            {
                node = list[0];
            }

            detail = node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? body;
        }
        catch (JsonException)
        {
            // corpo que não é JSON: vai como veio
        }

        if (detail.Length > 400)
        {
            detail = detail[..400] + "...";
        }

        var hint = (int)status switch
        {
            401 or 403 => "Chave de API recusada. Confira a chave nas configurações do assistente.",
            404 => "Modelo ou endereço não encontrado. Confira o modelo e o endereço nas configurações do assistente.",
            429 => "Limite de uso do provedor atingido (tentei 4 vezes). Espere um pouco ou troque de modelo nas configurações.",
            >= 500 => "O provedor continua sobrecarregado depois de 4 tentativas. Tente de novo em instantes ou troque de modelo nas configurações.",
            _ => "O provedor recusou o pedido.",
        };
        return new ChatModelException($"{hint}\n({(int)status}) {detail}".TrimEnd());
    }
}
