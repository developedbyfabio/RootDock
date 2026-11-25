using System.Text;

namespace RootDock.Core.Assistant;

/// <summary>
/// Transforma a saída bruta de um terminal (cores, movimentos de cursor, linha reescrita) no
/// texto que a pessoa enxerga, para o assistente ler. Não é um emulador: cobre o que aparece
/// num shell comum (prompt, comandos, saída) e ignora o resto.
/// </summary>
public static class TerminalText
{
    /// <summary>As últimas <paramref name="maxLines"/> linhas visíveis de <paramref name="raw"/>.</summary>
    public static string Tail(string raw, int maxLines)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var column = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '\u001b':
                    i = SkipEscape(raw, i, line, ref column);
                    break;

                case '\n':
                    lines.Add(line.ToString().TrimEnd());
                    line.Clear();
                    column = 0;
                    break;

                case '\r':
                    column = 0; // o que vier depois escreve por cima (barra de progresso, prompt redesenhado)
                    break;

                case '\b':
                    column = Math.Max(0, column - 1);
                    break;

                case '\t':
                    Put(line, ref column, ' ');
                    while (column % 8 != 0)
                    {
                        Put(line, ref column, ' ');
                    }

                    break;

                default:
                    if (c >= ' ')
                    {
                        Put(line, ref column, c);
                    }

                    break;
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString().TrimEnd());
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    private static void Put(StringBuilder line, ref int column, char c)
    {
        if (column < line.Length)
        {
            line[column] = c;
        }
        else
        {
            line.Append(' ', column - line.Length).Append(c);
        }

        column++;
    }

    /// <summary>Pula uma sequência de escape; aplica só "apagar até o fim da linha". Devolve o índice do último caractere dela.</summary>
    private static int SkipEscape(string raw, int start, StringBuilder line, ref int column)
    {
        if (start + 1 >= raw.Length)
        {
            return start;
        }

        var kind = raw[start + 1];
        if (kind == '[')
        {
            var i = start + 2;
            while (i < raw.Length && raw[i] is >= ' ' and <= '?')
            {
                i++;
            }

            if (i < raw.Length)
            {
                var parameters = raw.AsSpan(start + 2, i - start - 2);
                if (raw[i] == 'K' && (parameters.Length == 0 || parameters is "0") && column < line.Length)
                {
                    line.Length = column;
                }
                else if (raw[i] == 'G' || (raw[i] == 'H' && parameters.Length == 0))
                {
                    column = 0;
                }
            }

            return Math.Min(i, raw.Length - 1);
        }

        if (kind is ']' or 'P' or '_' or '^')
        {
            // OSC e afins: terminam em BEL ou ESC \
            for (var i = start + 2; i < raw.Length; i++)
            {
                if (raw[i] == '\u0007')
                {
                    return i;
                }

                if (raw[i] == '\u001b' && i + 1 < raw.Length && raw[i + 1] == '\\')
                {
                    return i + 1;
                }
            }

            return raw.Length - 1;
        }

        // ESC ( B, ESC ) 0...: escolha de conjunto de caracteres (três caracteres)
        return kind is '(' or ')' or '*' or '+' ? Math.Min(start + 2, raw.Length - 1) : start + 1;
    }
}
