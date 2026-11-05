using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using RootDock.Core.Security;

namespace RootDock.Core.Ssh;

/// <summary>
/// Monta autenticação e verificação de chave do servidor para qualquer cliente SSH.NET
/// (terminal, SFTP), garantindo as mesmas regras em todas as conexões.
/// </summary>
internal static class SshClientFactory
{
    /// <summary>Sem resposta do servidor nesse prazo (endereço errado, firewall), desiste.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Depois que o servidor responde, cada etapa pode esperar a pessoa: conferir a chave do
    /// servidor, digitar a senha ou o código 2FA. Esse tempo não pode derrubar a conexão.
    /// </summary>
    public static readonly TimeSpan InteractionTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Conexões em andamento (para a verificação da chave saber em que prazo está).</summary>
    private static readonly ConditionalWeakTable<BaseClient, ConnectAttempt> Attempts = new();

    private sealed class ConnectAttempt(CancellationTokenSource timeout)
    {
        private const int Waiting = 0;
        private const int Responded = 1;
        private const int Gave = 2;
        private int _state;

        public CancellationTokenSource Timeout { get; } = timeout;

        /// <summary>Desistimos (servidor travou no handshake): se a chave chegar depois, é recusada sem perguntar.</summary>
        public bool Abandoned => Volatile.Read(ref _state) == Gave;

        /// <summary>A chave do servidor chegou (o prazo curto deixou de valer).</summary>
        public bool HasResponded => Volatile.Read(ref _state) == Responded;

        /// <summary>A chave chegou a tempo (false se já desistimos).</summary>
        public bool MarkResponded() => Interlocked.CompareExchange(ref _state, Responded, Waiting) != Gave;

        /// <summary>Prazo estourado (false se a chave chegou antes: então não desiste).</summary>
        public bool MarkAbandoned() => Interlocked.CompareExchange(ref _state, Gave, Waiting) == Waiting;
    }

    /// <summary>
    /// Conecta com dois prazos: <see cref="ResponseTimeout"/> até o servidor mandar a chave dele
    /// (prova de que está respondendo) e <see cref="InteractionTimeout"/> daí em diante, para a
    /// pessoa conferir a chave ou digitar senha/2FA com calma.
    /// </summary>
    public static async Task ConnectAsync(BaseClient client, CancellationToken ct, TimeSpan? responseTimeout = null)
    {
        var limit = responseTimeout ?? ResponseTimeout;
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var attempt = new ConnectAttempt(timeout);
        Attempts.AddOrUpdate(client, attempt);
        try
        {
            // Numa thread do pool: o ConnectAsync do SSH.NET às vezes roda de forma síncrona até o
            // fim da troca de chaves (quando o socket conecta na hora), e aí bloquearia quem chamou
            // e o prazo abaixo nem começaria a contar.
            var connect = Task.Run(() => client.ConnectAsync(linked.Token), CancellationToken.None);
            // O SSH.NET espera a troca de chaves de forma síncrona, sem olhar o token: se o servidor
            // manda o banner e trava, só o prazo próprio dele (minutos) acabaria a espera.
            await Task.WhenAny(connect, Task.Delay(System.Threading.Timeout.Infinite, linked.Token)).ConfigureAwait(false);
            if (!connect.IsCompleted && ct.IsCancellationRequested)
            {
                attempt.MarkAbandoned();
                _ = connect.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
            }

            if (!connect.IsCompleted && !attempt.MarkAbandoned())
            {
                // A chave chegou no limite do prazo: segue esperando normalmente.
                await connect.ConfigureAwait(false);
                return;
            }

            if (!connect.IsCompleted)
            {
                _ = connect.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new SshOperationTimeoutException($"O servidor não respondeu em {limit.TotalSeconds:0} segundos.");
            }

            try
            {
                await connect.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or SshOperationTimeoutException) &&
                                       timeout.IsCancellationRequested && !ct.IsCancellationRequested && !attempt.HasResponded)
            {
                // O prazo venceu e, ao cancelar, o SSH.NET fecha a conexão: o erro que sobra
                // ("conexão encerrada") esconderia o motivo real, que é o servidor não responder.
                throw new SshOperationTimeoutException($"O servidor não respondeu em {limit.TotalSeconds:0} segundos.", ex);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SshOperationTimeoutException($"O servidor não respondeu em {limit.TotalSeconds:0} segundos.");
        }
        finally
        {
            if (!attempt.Abandoned)
            {
                Attempts.Remove(client);
            }
        }
    }

    /// <summary>A chave do servidor chegou: false se já desistimos desta conexão.</summary>
    private static bool ServerResponded(BaseClient client)
    {
        if (!Attempts.TryGetValue(client, out var attempt))
        {
            return true;
        }

        if (!attempt.MarkResponded())
        {
            return false;
        }

        try
        {
            attempt.Timeout.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // conexão já terminou
        }

        return true;
    }

    public static ConnectionInfo CreateConnectionInfo(
        string host, int port, SshCredentials credentials, ISshInteraction interaction, List<IDisposable> owned)
    {
        var methods = new List<AuthenticationMethod>();
        var user = credentials.Username;

        // Chaves do agente (ssh-agent do Windows / Pageant): a parte privada fica no agente.
        if (credentials.UseAgent)
        {
            try
            {
                if (SshAgentClient.Find() is { } agent && agent.KeySources() is { Count: > 0 } sources)
                {
                    methods.Add(new PrivateKeyAuthenticationMethod(user, [.. sources]));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
            {
                // Agente indisponível agora: segue com os outros métodos.
            }
        }

        if (!string.IsNullOrWhiteSpace(credentials.PrivateKeyPath))
        {
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(credentials.KeyPassphrase)
                    ? new PrivateKeyFile(credentials.PrivateKeyPath)
                    : new PrivateKeyFile(credentials.PrivateKeyPath, credentials.KeyPassphrase);
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new PrivateKeyPassphraseRequiredException(credentials.PrivateKeyPath, ex);
            }

            owned.Add(key);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }

        if (credentials.Password is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(user, credentials.Password));
        }

        // Muitos servidores pedem a senha via keyboard-interactive (e 2FA).
        var interactive = new KeyboardInteractiveAuthenticationMethod(user);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                var isPassword = !prompt.IsEchoed &&
                    prompt.Request.Contains("assword", StringComparison.OrdinalIgnoreCase);
                prompt.Response = isPassword && credentials.Password is not null
                    ? credentials.Password
                    : interaction.AnswerPrompt(host, e.Instruction, prompt.Request, prompt.IsEchoed) ?? "";
            }
        };
        methods.Add(interactive);

        return new ConnectionInfo(host, port, user, methods.ToArray())
        {
            Timeout = InteractionTimeout,
        };
    }

    /// <summary>Verifica a chave do servidor; pergunta ao usuário quando é nova ou mudou.</summary>
    public static void AttachHostKeyVerification(
        BaseClient client, string host, int port, KnownHostsStore knownHosts, ISshInteraction interaction, Action onRejected)
    {
        client.HostKeyReceived += (_, e) =>
        {
            if (!ServerResponded(client))
            {
                e.CanTrust = false; // conexão abandonada por demora: não abre diálogo para ela
                return;
            }

            // Daqui em diante o tempo é da pessoa (pode demorar para conferir a chave).
            var check = knownHosts.Check(host, port, e.HostKey, e.HostKeyName);
            if (check.Status == HostKeyStatus.Trusted)
            {
                e.CanTrust = true;
                return;
            }

            e.CanTrust = interaction.ConfirmHostKey(host, port, check, e.HostKey);
            if (e.CanTrust)
            {
                // "Só desta vez" vale para as outras conexões desta execução (SFTP, divisões).
                knownHosts.TrustForSession(host, port, e.HostKey);
            }
            else
            {
                onRejected();
            }
        };
    }
}
