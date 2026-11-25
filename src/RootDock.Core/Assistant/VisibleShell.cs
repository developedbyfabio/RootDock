using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RootDock.Core.MultiExec;

namespace RootDock.Core.Assistant;

/// <summary>O terminal visível não está num prompt que dê para acompanhar (programa aberto, shell sem integração).</summary>
public sealed class VisibleShellUnavailableException(string message) : Exception(message);

/// <summary>
/// Roda os comandos do assistente no terminal que a pessoa está vendo, digitando como ela
/// digitaria, e acompanha cada um até o fim. Funciona como a integração de shell do VS Code:
/// um trecho carregado uma vez no shell (bash ou zsh) faz o prompt emitir marcas invisíveis no
/// começo e no fim de cada comando, com o código de retorno. As marcas levam um código sorteado
/// por terminal, então a saída de um comando não consegue se passar por elas.
/// </summary>
public sealed class VisibleShell
{
    private const int MaxRawChars = 1024 * 1024;

    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private readonly string _prefix;
    private readonly Action<string> _type;
    private readonly object _gate = new();
    private readonly StringBuilder _carry = new();
    private Run? _run;
    private TaskCompletionSource<bool>? _activation;
    private bool _atPrompt;
    private bool _hasStartMark;

    /// <param name="type">Digita no terminal visível (como o teclado).</param>
    public VisibleShell(Action<string> type)
    {
        _type = type;
        _prefix = $"\u001b]7777;{_token};";
    }

    /// <summary>A última marca vista foi a de prompt: o shell está livre para receber um comando.</summary>
    public bool AtPrompt
    {
        get
        {
            lock (_gate)
            {
                return _atPrompt;
            }
        }
    }

    /// <summary>
    /// O trecho que o shell carrega (". arquivo"). Guarda o código de retorno antes de qualquer
    /// outra coisa do prompt e o devolve intacto, para não mudar o que o prompt da pessoa mostra.
    /// </summary>
    public string Script() =>
        "# RootDock: marca o comeco e o fim de cada comando para o assistente acompanhar.\n" +
        "if [ -n \"${BASH_VERSION-}\" ]; then\n" +
        $"  __rootdock_done() {{ local __rd=$?; printf '\\033]7777;{_token};D;%s\\007' \"$__rd\"; return $__rd; }}\n" +
        "  case \"${PROMPT_COMMAND-}\" in\n" +
        "    *__rootdock_done*) ;;\n" +
        "    *) PROMPT_COMMAND=\"__rootdock_done${PROMPT_COMMAND:+; $PROMPT_COMMAND}\" ;;\n" +
        "  esac\n" +
        "  [ -n \"${__ROOTDOCK_PS0+x}\" ] || __ROOTDOCK_PS0=\"${PS0-}\"\n" +
        $"  PS0=\"${{__ROOTDOCK_PS0}}\"$'\\033]7777;{_token};C\\007'\n" +
        "elif [ -n \"${ZSH_VERSION-}\" ]; then\n" +
        $"  __rootdock_done() {{ local __rd=$?; printf '\\033]7777;{_token};D;%s\\007' \"$__rd\"; return $__rd; }}\n" +
        $"  __rootdock_start() {{ printf '\\033]7777;{_token};C\\007'; }}\n" +
        "  (( ${precmd_functions[(I)__rootdock_done]} )) || precmd_functions=(__rootdock_done $precmd_functions)\n" +
        "  (( ${preexec_functions[(I)__rootdock_start]} )) || preexec_functions+=(__rootdock_start)\n" +
        "fi\n" +
        // Apaga da tela a linha ". arquivo" que acabou de ser digitada: a pessoa vê só os comandos de verdade.
        "[ -z \"${BASH_VERSION-}${ZSH_VERSION-}\" ] || printf '\\033[1A\\r\\033[2K'\n";

    /// <summary>
    /// Carrega o trecho no shell que está no terminal (digita ". arquivo") e espera a primeira
    /// marca de prompt. false = o shell não respondeu (fish, csh, tmux, programa aberto).
    /// </summary>
    public async Task<bool> ActivateAsync(string scriptPath, TimeSpan wait, CancellationToken ct)
    {
        TaskCompletionSource<bool> activation;
        lock (_gate)
        {
            _atPrompt = false;
            _activation = activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Ctrl+U limpa o que estiver digitado pela metade; o espaço na frente evita o histórico onde isso vale.
        _type($"\u0015 . {Sftp.RemotePath.ShellQuote(scriptPath)}\r");
        try
        {
            await activation.Task.WaitAsync(wait, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _activation = null;
            }
        }
    }

    /// <summary>
    /// Digita o comando no terminal e espera o prompt voltar. Devolve o que apareceu na tela e o
    /// código de retorno. Cancelar ou estourar o tempo manda Ctrl+C para o comando.
    /// </summary>
    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        if (command.Contains('\n', StringComparison.Ordinal))
        {
            throw new VisibleShellUnavailableException("comando de várias linhas");
        }

        Run run;
        bool expectStart;
        lock (_gate)
        {
            if (!_atPrompt || _run is not null)
            {
                throw new VisibleShellUnavailableException("o terminal não está no prompt");
            }

            _atPrompt = false;
            _run = run = new Run();
            expectStart = _hasStartMark;
        }

        try
        {
            _type("\u0015" + command + "\r");
            if (expectStart)
            {
                // Este shell marca o começo dos comandos; se a marca não vier, o shell em foco já é outro
                // (su, ssh, subshell) e não há como acompanhar o que foi digitado.
                try
                {
                    await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new IOException(
                        "O comando foi digitado no terminal, mas o shell em foco mudou (su, ssh ou outro shell) e não deu para acompanhar. " +
                        "Use read_terminal para ver o que aconteceu.");
                }
            }

            int exit;
            try
            {
                exit = await run.Done.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _type("\u0003"); // Ctrl+C no comando em andamento
                try
                {
                    await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // não voltou ao prompt: fica como está, a pessoa vê no terminal
                }

                throw;
            }

            string raw;
            bool started;
            lock (_gate)
            {
                raw = run.Raw.ToString();
                started = run.SawStart;
            }

            if (!started)
            {
                // Shell sem marca de começo (bash antigo): a primeira linha é o eco do próprio comando.
                var newline = raw.IndexOf('\n', StringComparison.Ordinal);
                raw = newline >= 0 ? raw[(newline + 1)..] : "";
            }

            var text = TerminalText.Tail(raw, int.MaxValue);
            return new CommandResult(exit, text.Length > 0 ? text + "\n" : "", "");
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
            }
        }
    }

    /// <summary>Recebe tudo o que o servidor manda para o terminal (thread de leitura).</summary>
    public void Feed(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _carry.Append(text);
            var data = _carry.ToString();
            _carry.Clear();
            var position = 0;
            while (position < data.Length)
            {
                var mark = data.IndexOf(_prefix, position, StringComparison.Ordinal);
                if (mark < 0)
                {
                    // Um começo de marca cortado no fim do pacote espera o resto.
                    var keep = PartialPrefixAt(data, position);
                    Capture(data.AsSpan(position, keep - position));
                    _carry.Append(data, keep, data.Length - keep);
                    return;
                }

                Capture(data.AsSpan(position, mark - position));
                var end = data.IndexOf('\u0007', mark + _prefix.Length);
                if (end < 0)
                {
                    if (data.Length - mark > 64)
                    {
                        position = mark + _prefix.Length; // marca que não fecha: não é nossa
                        continue;
                    }

                    _carry.Append(data, mark, data.Length - mark);
                    return;
                }

                Handle(data.AsSpan(mark + _prefix.Length, end - mark - _prefix.Length));
                position = end + 1;
            }
        }
    }

    /// <summary>
    /// Este shell avisa quando um comando começa (bash 4.4+ e zsh)? Sem esse aviso não dá para
    /// saber, só pelas marcas, que a pessoa abriu um programa no terminal.
    /// </summary>
    public bool HasStartMark
    {
        get
        {
            lock (_gate)
            {
                return _hasStartMark;
            }
        }
    }

    /// <summary>O terminal reconectou: o shell é outro e a integração precisa ser carregada de novo.</summary>
    public void Reset()
    {
        Close();
        lock (_gate)
        {
            _hasStartMark = false;
        }
    }

    /// <summary>O terminal desconectou ou fechou: quem espera um comando é liberado com erro.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _atPrompt = false;
            _run?.Done.TrySetException(new IOException("O terminal foi fechado ou desconectou durante o comando."));
            _activation?.TrySetException(new IOException("O terminal foi fechado ou desconectou."));
        }
    }

    private void Handle(ReadOnlySpan<char> body)
    {
        if (body is "C")
        {
            _hasStartMark = true;
            _atPrompt = false;
            if (_run is { SawStart: false } run)
            {
                run.SawStart = true;
                run.Raw.Clear(); // o que veio antes era o eco do comando digitado
                run.Started.TrySetResult(true);
            }

            return;
        }

        if (body.Length > 2 && body[0] == 'D' && body[1] == ';' &&
            int.TryParse(body[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var exit))
        {
            _atPrompt = true;
            _activation?.TrySetResult(true);
            if (_run is { } run && (run.SawStart || !_hasStartMark))
            {
                run.Done.TrySetResult(exit);
            }
        }
    }

    private void Capture(ReadOnlySpan<char> text)
    {
        if (_run is not { } run || text.IsEmpty || run.Done.Task.IsCompleted)
        {
            return;
        }

        run.Raw.Append(text);
        if (run.Raw.Length > MaxRawChars)
        {
            // Saída enorme: fica o começo e o fim (o meio não caberia na conversa de qualquer forma).
            const string Omitted = "\n[... saída omitida ...]\n";
            var head = MaxRawChars / 4;
            if (!run.Trimmed)
            {
                run.Trimmed = true;
                run.Raw.Insert(head, Omitted);
            }

            var from = head + Omitted.Length;
            run.Raw.Remove(from, run.Raw.Length - from - MaxRawChars / 2);
        }
    }

    /// <summary>Índice a partir do qual o fim de <paramref name="data"/> pode ser o começo de uma marca.</summary>
    private int PartialPrefixAt(string data, int from)
    {
        var escape = data.LastIndexOf('\u001b');
        if (escape >= from && data.Length - escape < _prefix.Length &&
            _prefix.AsSpan().StartsWith(data.AsSpan(escape), StringComparison.Ordinal))
        {
            return escape;
        }

        return data.Length;
    }

    private sealed class Run
    {
        public StringBuilder Raw { get; } = new();

        public bool SawStart { get; set; }

        public bool Trimmed { get; set; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
