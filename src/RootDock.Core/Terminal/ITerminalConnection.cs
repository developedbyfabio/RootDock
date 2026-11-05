namespace RootDock.Core.Terminal;

/// <summary>
/// Uma conexão de terminal (SSH ou shell local) que troca bytes com a tela.
/// Ciclo: <see cref="ConnectAsync"/> (estabelece) → assinar eventos → <see cref="Start"/> (começa a entregar dados).
/// Nada recebido entre o connect e o Start se perde: fica no buffer do canal/pipe.
/// </summary>
public interface ITerminalConnection : IDisposable
{
    /// <summary>
    /// Bytes recebidos do servidor/processo. Disparado em thread de fundo.
    /// O buffer só é válido durante a chamada: copie ou decodifique antes de retornar.
    /// Bloquear o handler aplica contrapressão (pausa a leitura).
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Conexão encerrada (uma única vez). O argumento é o motivo, quando houve erro.</summary>
    event Action<string?>? Closed;

    bool IsConnected { get; }

    /// <summary>Abre a conexão (rede, autenticação, processo).</summary>
    Task ConnectAsync(int columns, int rows, CancellationToken cancellationToken);

    /// <summary>Começa a entregar dados. Se a conexão já caiu, dispara <see cref="Closed"/> na hora.</summary>
    void Start();

    /// <summary>Envia bytes. Não bloqueia: a escrita real acontece em fila de fundo.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);
}
