using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
public sealed partial class VisibleShell
{
    private const int MaxRawChars = 1024 * 1024;

    /// <summary>Quanto tempo parado num prompt de paginador até concluir que ele espera uma tecla.</summary>
    private static readonly TimeSpan PagerIdle = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan PagerPoll = TimeSpan.FromMilliseconds(400);
    private const int MaxPagerExits = 3;

    /// <summary>Variáveis que mandam journalctl, systemctl, git, man e afins abrirem um paginador.</summary>
    private const string PagerVariables = "PAGER SYSTEMD_PAGER GIT_PAGER MANPAGER";

    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private readonly string _prefix;
    private readonly Action<string> _type;
    private readonly object _gate = new();
    private readonly StringBuilder _carry = new();
    private Run? _run;
    private TaskCompletionSource<bool>? _activation;
    private TaskCompletionSource<bool>? _prepare;
    private bool _atPrompt;
    private bool _hasStartMark;

    /// <summary>
    /// Antes de cada comando, desliga os paginadores só para ele (e devolve depois): no terminal,
    /// journalctl, systemctl e git log abririam o less e ficariam esperando uma tecla para sempre.
    /// O que a pessoa digita por conta própria continua paginando como sempre.
    /// </summary>
    public bool QuietPagers { get; init; }

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
        // __rootdock_quiet desliga os paginadores para o proximo comando; o prompt seguinte devolve como estava.
        "__rootdock_quiet() {\n" +
        "  local __v\n" +
        $"  for __v in {PagerVariables}; do\n" +
        "    [ -n \"${__rootdock_q-}\" ] || eval \"__rootdock_was_$__v=\\\"\\${$__v-__rootdock_none}\\\"\"\n" +
        "    export \"$__v=cat\"\n" +
        "  done\n" +
        "  __rootdock_q=1\n" +
        "  printf '\\033[1A\\r\\033[2K'\n" +
        "}\n" +
        "__rootdock_pagers() {\n" +
        "  local __v\n" +
        "  case \"${__rootdock_q-}\" in\n" +
        "    1) __rootdock_q=2 ;;\n" +
        "    2) __rootdock_q=\n" +
        $"       for __v in {PagerVariables}; do\n" +
        "         eval \"if [ \\\"\\${$__v-}\\\" != cat ]; then :; elif [ \\\"\\$__rootdock_was_$__v\\\" = __rootdock_none ]; then unset $__v; else $__v=\\\"\\$__rootdock_was_$__v\\\"; fi\"\n" +
        "       done ;;\n" +
        "  esac\n" +
        "}\n" +
        "if [ -n \"${BASH_VERSION-}\" ]; then\n" +
        $"  __rootdock_done() {{ local __rd=$?; __rootdock_pagers; printf '\\033]7777;{_token};D;%s\\007' \"$__rd\"; return $__rd; }}\n" +
        "  case \"${PROMPT_COMMAND-}\" in\n" +
        "    *__rootdock_done*) ;;\n" +
        "    *) PROMPT_COMMAND=\"__rootdock_done${PROMPT_COMMAND:+; $PROMPT_COMMAND}\" ;;\n" +
        "  esac\n" +
        "  [ -n \"${__ROOTDOCK_PS0+x}\" ] || __ROOTDOCK_PS0=\"${PS0-}\"\n" +
        $"  PS0=\"${{__ROOTDOCK_PS0}}\"$'\\033]7777;{_token};C\\007'\n" +
        "elif [ -n \"${ZSH_VERSION-}\" ]; then\n" +
        $"  __rootdock_done() {{ local __rd=$?; __rootdock_pagers; printf '\\033]7777;{_token};D;%s\\007' \"$__rd\"; return $__rd; }}\n" +
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
        TaskCompletionSource<bool>? prepare = null;
        lock (_gate)
        {
            if (!_atPrompt || _run is not null || _prepare is not null)
            {
                throw new VisibleShellUnavailableException("o terminal não está no prompt");
            }

            _atPrompt = false;
            run = new Run();
            if (QuietPagers)
            {
                _prepare = prepare = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _run = run;
            }

            expectStart = _hasStartMark;
        }

        try
        {
            if (prepare is not null)
            {
                // Linha de preparo: apaga a si mesma da tela, e o prompt volta no mesmo lugar.
                _type("\u0015 __rootdock_quiet\r");
                try
                {
                    await prepare.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new VisibleShellUnavailableException("o terminal não voltou ao prompt");
                }

                lock (_gate)
                {
                    _prepare = null;
                    _atPrompt = false;
                    _run = run;
                    run.LastOutput = Environment.TickCount64;
                }
            }

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
                exit = await WaitDoneAsync(run, timeout, ct).ConfigureAwait(false);
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
            bool paged;
            lock (_gate)
            {
                raw = run.Raw.ToString();
                started = run.SawStart;
                paged = run.PagerExits > 0;
            }

            if (!started)
            {
                // Shell sem marca de começo (bash antigo): a primeira linha é o eco do próprio comando.
                var newline = raw.IndexOf('\n', StringComparison.Ordinal);
                raw = newline >= 0 ? raw[(newline + 1)..] : "";
            }

            var text = TerminalText.Tail(raw, int.MaxValue);
            if (paged)
            {
                text += "\n[A saída abriu num paginador (less/more), que ficou esperando uma tecla; ele foi fechado e só a primeira tela " +
                        "está acima. Rode de novo sem paginador: --no-pager, ou termine o comando com \"| cat\".]";
            }

            return new CommandResult(exit, text.Length > 0 ? text + "\n" : "", "");
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
                _prepare = null;
            }
        }
    }

    /// <summary>
    /// Espera o prompt voltar. Enquanto espera, confere se o comando parou num paginador (um que
    /// ignora as variáveis, ou um shell aberto por dentro com su/ssh): ali ele ficaria para sempre
    /// esperando uma tecla, então o paginador é fechado com "q" e o resultado avisa o modelo.
    /// </summary>
    private async Task<int> WaitDoneAsync(Run run, TimeSpan timeout, CancellationToken ct)
    {
        var limited = timeout != Timeout.InfiniteTimeSpan;
        var deadline = limited ? Environment.TickCount64 + (long)timeout.TotalMilliseconds : long.MaxValue;
        while (true)
        {
            var wait = PagerPoll;
            if (limited)
            {
                var left = TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));
                if (left == TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                wait = left < PagerPoll ? left : PagerPoll;
            }

            try
            {
                return await run.Done.Task.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // ainda rodando: confere o paginador e continua esperando
            }

            var quit = false;
            lock (_gate)
            {
                if (run.PagerExits < MaxPagerExits && !run.Done.Task.IsCompleted &&
                    Environment.TickCount64 - run.LastOutput >= PagerIdle.TotalMilliseconds && AtPagerPrompt(run.Raw))
                {
                    run.PagerExits++;
                    run.LastOutput = Environment.TickCount64;
                    quit = true;
                }
            }

            if (quit)
            {
                _type("q");
            }
        }
    }

    /// <summary>A última linha da tela é o prompt de um paginador (less, more, man)?</summary>
    internal static bool AtPagerPrompt(StringBuilder raw)
    {
        var from = Math.Max(0, raw.Length - 400);
        var tail = TerminalText.Tail(raw.ToString(from, raw.Length - from), 1).Trim();
        return tail.Length is > 0 and < 200 && PagerPrompt().IsMatch(tail);
    }

    [GeneratedRegex(@"^(?:lines \d+-\d+(?:/\d+)?(?: \(END\))?(?: .*)?|\(END\)|:|-- ?More ?--.*|Manual page .+ line \d+.*|.*\(press h for help or q to quit\))$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PagerPrompt();

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
            _prepare?.TrySetException(new IOException("O terminal foi fechado ou desconectou durante o comando."));
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
            _prepare?.TrySetResult(true);
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
        run.LastOutput = Environment.TickCount64;
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

        /// <summary>Quando chegou a última saída (para saber se o comando parou).</summary>
        public long LastOutput { get; set; } = Environment.TickCount64;

        /// <summary>Quantas vezes um paginador foi fechado neste comando.</summary>
        public int PagerExits { get; set; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
