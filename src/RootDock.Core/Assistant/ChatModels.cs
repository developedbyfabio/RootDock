using System.Text.Json;

namespace RootDock.Core.Assistant;

public enum ChatRole
{
    User,
    Assistant,

    /// <summary>Resultados das ferramentas pedidas pela resposta anterior.</summary>
    Tool,
}

/// <summary>Uma ferramenta oferecida ao modelo. <paramref name="Schema"/> é o JSON Schema dos parâmetros.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Schema, bool ReadOnly);

/// <summary>Pedido do modelo para usar uma ferramenta.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);

public sealed record ToolResult(string CallId, string Content, bool IsError);

/// <summary>Uma imagem que a pessoa anexou à mensagem (print de tela, foto de um erro).</summary>
public sealed record ChatImage(string MediaType, string Base64);

/// <summary>Uma entrada da conversa, no formato comum a todos os provedores.</summary>
public sealed class ChatTurn
{
    public required ChatRole Role { get; init; }

    public string Text { get; init; } = "";

    /// <summary>Imagens anexadas pela pessoa (só em mensagens dela).</summary>
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    public IReadOnlyList<ToolCall> Calls { get; init; } = [];

    public IReadOnlyList<ToolResult> Results { get; init; } = [];

    /// <summary>
    /// A resposta como o provedor a entregou (blocos de raciocínio assinados, campos extras das
    /// chamadas de ferramenta). Só o provedor que a criou a entende; ele a devolve intacta.
    /// </summary>
    public object? Native { get; init; }

    /// <summary>Por que a resposta parou quando não foi o fim normal (ex.: limite de tamanho, recusa).</summary>
    public string? StopNote { get; init; }

    public static ChatTurn FromUser(string text, IReadOnlyList<ChatImage>? images = null) =>
        new() { Role = ChatRole.User, Text = text, Images = images ?? [] };

    public static ChatTurn FromResults(IReadOnlyList<ToolResult> results) => new() { Role = ChatRole.Tool, Results = results };
}

/// <summary>Um modelo de conversa com ferramentas (Claude, Gemini, OpenAI e compatíveis).</summary>
public interface IChatModel
{
    /// <summary>
    /// Pede a próxima resposta. <paramref name="onText"/> recebe o texto conforme chega.
    /// Erros do provedor saem como <see cref="ChatModelException"/> com mensagem para a pessoa.
    /// </summary>
    Task<ChatTurn> CompleteAsync(
        string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolDefinition> tools, Action<string> onText, CancellationToken ct);
}

/// <summary>Falha do provedor já explicada em português (chave inválida, limite, modelo inexistente...).</summary>
public sealed class ChatModelException(string message, Exception? inner = null) : Exception(message, inner);
