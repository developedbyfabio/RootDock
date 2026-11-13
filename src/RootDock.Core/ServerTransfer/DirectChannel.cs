using System.Globalization;
using System.Text;
using Renci.SshNet;

namespace RootDock.Core.ServerTransfer;

/// <summary>Como o servidor de origem chega no de destino.</summary>
/// <param name="KnownHostLines">Chave(s) do destino já conferidas pelo RootDock, no formato do known_hosts.</param>
public sealed record DirectEndpoint(string Host, int Port, string User, IReadOnlyList<string> KnownHostLines);

/// <summary>
/// Modo direto: o servidor de origem roda o rsync e fala com o de destino por SSH, sem os dados
/// passarem por este computador. Para a origem entrar no destino, cria uma chave só para esta
/// janela (a parte privada nunca sai da origem), autoriza no destino e apaga dos dois ao fechar.
/// A origem só aceita a chave de servidor do destino que o RootDock já conferiu.
/// </summary>
public sealed class DirectChannel
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    private readonly SshClient _source;
    private readonly SshClient _target;
    private readonly DirectEndpoint _endpoint;
    private readonly string _directory;
    private readonly string _comment;
    private int _sequence;
    private int _closed;

    private DirectChannel(SshClient source, SshClient target, DirectEndpoint endpoint, string directory, string comment)
    {
        _source = source;
        _target = target;
        _endpoint = endpoint;
        _directory = directory;
        _comment = comment;
    }

    /// <summary>
    /// Prepara o caminho direto e testa. Falha com <see cref="DirectUnavailableException"/> (com o
    /// motivo) e sem deixar nada para trás quando não dá: aí a cópia segue pelo RootDock.
    /// </summary>
    public static async Task<DirectChannel> OpenAsync(SshClient source, SshClient target, DirectEndpoint endpoint, CancellationToken ct)
    {
        if (!DirectScripts.IsUserName(endpoint.User) || !DirectScripts.IsHostName(endpoint.Host))
        {
            throw new DirectUnavailableException("o usuário ou o endereço do destino tem caracteres que o modo direto não aceita");
        }

        if (endpoint.KnownHostLines.Count == 0)
        {
            throw new DirectUnavailableException("a chave do servidor de destino ainda não foi salva (conecte uma vez pelo terminal)");
        }

        var probe = await RunAsync(source, DirectScripts.ProbeSource(), Short, ct).ConfigureAwait(false);
        var first = probe.Output.Trim();
        switch (first)
        {
            case "NO-RSYNC":
                throw new DirectUnavailableException("o servidor de origem não tem o rsync instalado");
            case "NO-SSH" or "NO-KEYGEN":
                throw new DirectUnavailableException("o servidor de origem não tem o cliente SSH (ssh e ssh-keygen)");
        }

        var version = RsyncOutput.ParseVersion(first);
        if (version is null)
        {
            throw new DirectUnavailableException("o servidor de origem não respondeu como um Linux com rsync");
        }

        if (version < RsyncOutput.MinimumVersion)
        {
            throw new DirectUnavailableException($"o rsync do servidor de origem é antigo demais ({version}; precisa do 3.1 ou mais novo)");
        }

        if ((await RunAsync(target, DirectScripts.ProbeTarget(), Short, ct).ConfigureAwait(false)).Output.Trim() != "OK")
        {
            throw new DirectUnavailableException("o servidor de destino não tem o rsync instalado");
        }

        var comment = DirectScripts.Comment(DateTime.UtcNow, Guid.NewGuid().ToString("N")[..12]);
        var created = await RunAsync(source, DirectScripts.CreateKey(comment), Short, ct).ConfigureAwait(false);
        var lines = created.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directory = lines.Length >= 1 ? lines[0] : "";
        var publicKey = lines.Length >= 2 ? DirectScripts.CleanPublicKey(lines[1]) : null;
        if (created.Exit != 0 || !DirectScripts.IsWorkDirectory(directory) || publicKey is null)
        {
            if (DirectScripts.IsWorkDirectory(directory))
            {
                await TryAsync(source, DirectScripts.RemoveWorkDirectory(directory)).ConfigureAwait(false);
            }

            throw new DirectUnavailableException("o servidor de origem não conseguiu criar a chave temporária" + Tail(created.Error));
        }

        var channel = new DirectChannel(source, target, endpoint, directory, comment);
        try
        {
            var known = await RunAsync(source, DirectScripts.WriteKnownHosts(directory, endpoint.KnownHostLines), Short, ct).ConfigureAwait(false);
            if (known.Exit != 0)
            {
                throw new DirectUnavailableException("o servidor de origem não conseguiu gravar na pasta temporária" + Tail(known.Error));
            }

            var stale = DateTime.UtcNow.AddHours(-24).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            var authorized = await RunAsync(target, DirectScripts.Authorize(publicKey, comment, stale), Short, ct).ConfigureAwait(false);
            if (authorized.Exit != 0)
            {
                throw new DirectUnavailableException("o servidor de destino não deixou autorizar a chave temporária" + Tail(authorized.Error));
            }

            var test = await RunAsync(source, DirectScripts.Test(directory, endpoint.User, endpoint.Host, endpoint.Port), Short, ct).ConfigureAwait(false);
            if (test.Exit != 0)
            {
                throw new DirectUnavailableException(
                    $"o servidor de origem não conseguiu entrar em {endpoint.Host}:{endpoint.Port} (rede bloqueada entre os dois, ou o destino não aceita chave)" + Tail(test.Error));
            }

            return channel;
        }
        catch
        {
            await channel.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Copia com o rsync. Pode ser chamado de novo depois de uma pausa ou queda: o rsync aproveita
    /// o que já está no destino (inclusive o pedaço do arquivo que estava no meio).
    /// </summary>
    /// <param name="writeSourceFile">Grava um arquivo no servidor de origem (a lista de itens vai por SFTP).</param>
    public async Task CopyAsync(
        CopyRequest request, Func<string, byte[], CancellationToken, Task> writeSourceFile,
        Action<long> setTotal, Action<long> setDone, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
        var listFile = $"{_directory}/list.{id}";
        var pidFile = $"{_directory}/pid.{id}";
        var list = new StringBuilder();
        foreach (var item in request.Items)
        {
            list.Append(item.RelativePath).Append('\0');
        }

        await writeSourceFile(listFile, Encoding.UTF8.GetBytes(list.ToString()), ct).ConfigureAwait(false);

        long total = request.Items.Where(i => !i.IsDirectory).Sum(i => i.Size);
        if (request.Items.Any(i => i.IsDirectory) && request.Items.Count <= 500)
        {
            var size = await RunAsync(_source, DirectScripts.TotalSize(request.SourceRoot, request.Items.Select(i => i.RelativePath)), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            total = long.TryParse(size.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
        }
        else if (request.Items.Any(i => i.IsDirectory))
        {
            total = 0;
        }

        var known = total > 0;
        if (known)
        {
            setTotal(total);
        }

        var script = DirectScripts.Rsync(_directory, pidFile, listFile, request.SourceRoot, request.TargetRoot,
            _endpoint.User, _endpoint.Host, _endpoint.Port, request.Preserve, request.SkipExisting);
        using var command = _source.CreateCommand(DirectScripts.Wrap(script));
        var execution = command.ExecuteAsync(ct);
        var progress = Task.Run(() => ReadProgress(command.OutputStream, p =>
        {
            if (!known && p.Percent > 0)
            {
                setTotal(p.Bytes * 100 / p.Percent); // sem o total (du sem -b): estima pelo percentual do rsync
            }

            setDone(p.Bytes);
        }), CancellationToken.None);
        var errors = Task.Run(() => ReadTail(command.ExtendedOutputStream), CancellationToken.None);
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryAsync(_source, DirectScripts.Kill(pidFile)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await TryAsync(_source, $"rm -f {Sftp.RemotePath.ShellQuote(listFile)} {Sftp.RemotePath.ShellQuote(pidFile)}").ConfigureAwait(false);
        }

        await progress.ConfigureAwait(false);
        var stderr = await errors.ConfigureAwait(false);
        var exit = command.ExitStatus ?? -1;
        if (exit != 0)
        {
            throw new IOException(RsyncOutput.DescribeExit(exit) + Tail(stderr));
        }
    }

    /// <summary>Tira a chave temporária do destino e apaga a pasta de trabalho da origem. Nunca lança.</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await TryAsync(_target, DirectScripts.Revoke(_comment)).ConfigureAwait(false);
        await TryAsync(_source, DirectScripts.RemoveWorkDirectory(_directory)).ConfigureAwait(false);
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(SshClient client, string script, TimeSpan timeout, CancellationToken ct)
    {
        using var command = client.CreateCommand(DirectScripts.Wrap(script));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await command.ExecuteAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DirectUnavailableException("um dos servidores demorou demais para responder");
        }

        return (command.ExitStatus ?? -1, command.Result, command.Error);
    }

    /// <summary>Limpeza: tenta e segue (conexão caída não pode impedir o resto).</summary>
    private static async Task TryAsync(SshClient client, string script)
    {
        try
        {
            if (client.IsConnected)
            {
                await RunAsync(client, script, Short, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // sem conexão: a limpeza das chaves antigas acontece na próxima vez (marca com data)
        }
    }

    /// <summary>O rsync reescreve a linha de andamento com \r: cada pedaço é uma leitura nova.</summary>
    private static void ReadProgress(Stream output, Action<RsyncProgress> report)
    {
        var buffer = new byte[4096];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = (char)buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (RsyncOutput.ParseProgress(line.ToString()) is { } progress)
                        {
                            report(progress);
                        }

                        line.Clear();
                    }
                    else if (line.Length < 512)
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or Renci.SshNet.Common.SshException)
        {
            // comando cancelado ou conexão caída: quem chamou trata
        }
    }

    private static string ReadTail(Stream stream)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (text.Length > 16384)
                {
                    text.Remove(0, text.Length - 8192);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or Renci.SshNet.Common.SshException)
        {
        }

        return text.ToString();
    }

    /// <summary>Últimas linhas úteis do erro, para a mensagem.</summary>
    private static string Tail(string? error)
    {
        var lines = (error ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : ": " + string.Join(" | ", lines.TakeLast(3));
    }
}
