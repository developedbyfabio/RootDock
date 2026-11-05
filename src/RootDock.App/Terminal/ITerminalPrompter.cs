namespace RootDock.App.Terminal;

/// <summary>
/// Perguntas feitas dentro do próprio terminal, como no PuTTY e no MobaXterm:
/// "Usuário:", "Senha de root@servidor:", códigos 2FA. Pode ser chamada de qualquer thread.
/// </summary>
public interface ITerminalPrompter
{
    /// <summary>
    /// Mostra o texto e lê a linha digitada (sem eco quando <paramref name="echo"/> é false).
    /// Retorna null quando a pessoa cancela (Ctrl+C) ou o painel é fechado.
    /// </summary>
    Task<string?> AskAsync(string prompt, bool echo, CancellationToken ct);

    /// <summary>Escreve texto no terminal (aceita sequências ANSI).</summary>
    void Write(string text);

    /// <summary>
    /// Cartão com botões sobre o terminal, sem bloquear o uso. Retorna o id do botão escolhido,
    /// ou null se a pessoa dispensou ou o painel fechou.
    /// </summary>
    Task<string?> OfferAsync(string text, IReadOnlyList<OfferAction> actions);
}

public sealed record OfferAction(string Id, string Label, bool Primary = false);
