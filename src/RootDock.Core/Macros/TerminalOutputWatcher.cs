using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RootDock.Core.Macros;

/// <summary>
/// Acompanha a saída de um terminal enquanto uma macro roda: tira as sequências de controle (cores,
/// cursor, títulos), mantém a linha atual (a do prompt) e o texto desde o último comando enviado.
/// Recebe pedaços de qualquer tamanho, inclusive com uma sequência de escape cortada no meio.
/// Seguro para ser alimentado pela thread de leitura e consultado pela da interface.
/// </summary>
public sealed partial class TerminalOutputWatcher
{
    private const int MaxLine = 1024;
    private const int MaxSinceMark = 64 * 1024;

    // Fim de prompt de shell: "root@srv:~# ", "user@srv:~$ ", "PS C:\> ", "C:\pasta>", zsh "host% "/"❯ ".
    // "%" só com espaço e sem número antes: "Lendo listas... 45%" é progresso, não prompt.
    [GeneratedRegex(@"^.{0,300}?(?:[#$>❯➜»λ] ?|(?<!\d)% )$")]
    private static partial Regex PromptEnd();

    private readonly object _gate = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _sinceMark = new();
    private readonly Func<long> _now;
    private State _state;
    private bool _carriageReturn;
    private int _linesSinceMark;
    private long _lastOutput;

    public TerminalOutputWatcher(Func<long>? clockMs = null)
    {
        var watch = Stopwatch.StartNew();
        _now = clockMs ?? (() => watch.ElapsedMilliseconds);
        _lastOutput = _now();
    }

    private enum State
    {
        Text,
        Escape,
        Csi,
        String,
        StringEscape,
        Charset,
    }

    /// <summary>Linha em que o cursor está (normalmente o prompt), sem cores.</summary>
    public string CurrentLine
    {
        get
        {
            lock (_gate)
            {
                return _line.ToString();
            }
        }
    }

    /// <summary>A linha atual tem cara de prompt do shell.</summary>
    public bool AtPrompt
    {
        get
        {
            var line = CurrentLine;
            return line.Trim().Length > 0 && PromptEnd().IsMatch(line);
        }
    }

    /// <summary>Quebras de linha recebidas desde o último <see cref="Mark"/> (o Enter do comando já conta uma).</summary>
    public int LinesSinceMark
    {
        get
        {
            lock (_gate)
            {
                return _linesSinceMark;
            }
        }
    }

    /// <summary>Tempo sem chegar nada do terminal.</summary>
    public TimeSpan IdleFor
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromMilliseconds(_now() - _lastOutput);
            }
        }
    }

    /// <summary>Um comando foi enviado: a partir daqui contam as linhas e o texto procurado.</summary>
    public void Mark()
    {
        lock (_gate)
        {
            _linesSinceMark = 0;
            _sinceMark.Clear();
        }
    }

    /// <summary>O texto apareceu (sem diferenciar maiúsculas) desde o último <see cref="Mark"/>.</summary>
    public bool SawSinceMark(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        lock (_gate)
        {
            return _sinceMark.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _lastOutput = _now();
            foreach (var c in text)
            {
                Feed(c);
            }

            if (_sinceMark.Length > MaxSinceMark)
            {
                _sinceMark.Remove(0, _sinceMark.Length - MaxSinceMark / 2);
            }
        }
    }

    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Escape:
                _state = c switch
                {
                    '[' => State.Csi,
                    ']' or 'P' or 'X' or '^' or '_' => State.String, // OSC (título, cor...), DCS, APC
                    '(' or ')' or '*' or '+' => State.Charset,       // ESC ( B: troca de conjunto de caracteres
                    _ => State.Text,                                 // ESC 7, ESC =, ...
                };
                return;
            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    _state = State.Text; // byte final (m, K, H, J...)
                }

                return;
            case State.String:
                if (c == '\u0007')
                {
                    _state = State.Text;
                }
                else if (c == '\u001b')
                {
                    _state = State.StringEscape;
                }

                return;
            case State.StringEscape:
                _state = c == '\\' ? State.Text : State.String;
                return;
            case State.Charset:
                _state = State.Text;
                return;
        }

        switch (c)
        {
            case '\u001b':
                _state = State.Escape;
                return;
            case '\n':
                _carriageReturn = false;
                _line.Clear();
                _linesSinceMark++;
                _sinceMark.Append('\n');
                return;
            case '\r':
                _carriageReturn = true; // o que vier depois reescreve a linha (barras de progresso, redesenho do prompt)
                return;
            case '\b':
                if (_line.Length > 0)
                {
                    _line.Length--;
                }

                return;
            case '\t':
                Print(' ');
                return;
        }

        if (c < ' ' || c == '\u007f')
        {
            return; // BEL e outros controles
        }

        Print(c);
    }

    private void Print(char c)
    {
        if (_carriageReturn)
        {
            _carriageReturn = false;
            _line.Clear();
        }

        _line.Append(c);
        if (_line.Length > MaxLine)
        {
            _line.Remove(0, _line.Length - MaxLine);
        }

        _sinceMark.Append(c);
    }
}
