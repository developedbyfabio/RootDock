using System.Windows;
using RootDock.App.Dialogs;
using RootDock.App.Rdp;
using RootDock.App.Services;
using RootDock.Core.Ftp;
using RootDock.Core.Security;
using RootDock.Core.Sessions;
using RootDock.Core.Sftp;

namespace RootDock.App.Remote;

/// <summary>
/// Sessões FTP/FTPS: pede o login (ou usa o salvo), conecta, trata certificado não confiável
/// (pergunta e lembra a impressão digital) e abre o gerenciador de arquivos.
/// </summary>
public sealed class FtpService
{
    private readonly Window _owner;
    private readonly SessionManager _sessions;
    private readonly SessionLogins _logins;
    private readonly TransferQueue _transfers;
    private readonly Core.History.TransferHistory _history;
    private readonly Action<string, bool> _notify;

    public FtpService(Window owner, CredentialVault vault, SessionManager sessions, TransferQueue transfers, Core.History.TransferHistory history, Action<string, bool> notify)
    {
        _owner = owner;
        _sessions = sessions;
        _logins = new SessionLogins(vault);
        _transfers = transfers;
        _history = history;
        _notify = notify;
    }

    public async Task OpenAsync(SessionEntry entry)
    {
        var settings = entry.Ftp ?? new FtpSettings();
        string? message = null;
        while (true)
        {
            if (Login(entry, message) is not { } credentials)
            {
                return;
            }

            FtpFileSystem fs;
            try
            {
                _notify($"Conectando a {entry.Host} ({(settings.Security == FtpSecurity.None ? "FTP" : "FTPS")})...", false);
                fs = await FtpFileSystem.ConnectAsync(entry.Host, entry.Port, credentials.Username, credentials.Password, settings, CancellationToken.None);
            }
            catch (FtpCertificateException cert)
            {
                var text = $"{cert.Problem}.\n\nPara: {cert.Subject}\nEmitido por: {cert.Issuer}\nVálido até: {cert.NotAfter:dd/MM/yyyy}\nSHA-256: {cert.Thumbprint}\n\n" +
                           "Confie só se você reconhece este certificado (ex.: confirme a impressão digital com quem administra o servidor). Ele fica lembrado nesta sessão.";
                if (!await Prompts.ConfirmAsync("Certificado não confiável", text, "Confiar neste certificado", danger: true))
                {
                    return;
                }

                settings = settings with { TrustedCertificate = cert.Thumbprint };
                if (_sessions.Document.Sessions.FirstOrDefault(s => s.Id == entry.Id) is { } saved)
                {
                    saved.Ftp = settings;
                    _sessions.Save();
                }

                entry.Ftp = settings;
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                message = ex.Message;
                continue;
            }
            catch (Exception ex)
            {
                Log.Info($"FTP {entry.Host}: {ex.Message}");
                await Prompts.InfoAsync("Não foi possível conectar", Explain(ex, entry));
                return;
            }

            Remember(entry, credentials);
            _sessions.Touch(entry);
            Open(entry, fs, credentials, settings);
            return;
        }
    }

    private void Open(SessionEntry entry, FtpFileSystem first, RdpCredentials credentials, FtpSettings settings)
    {
        IFileTransport current = first;
        var gate = new SemaphoreSlim(1, 1);
        var source = new FileManagerSource
        {
            Name = entry.Name,
            IsProduction = entry.Environment == SessionEnvironment.Production,
            StartPath = first.HomeDirectory,
            Protocol = first.Protocol,
            Connect = async () =>
            {
                await gate.WaitAsync();
                try
                {
                    if (!current.IsConnected)
                    {
                        // Servidores FTP derrubam conexões paradas: reconecta com o mesmo login.
                        current.Dispose();
                        current = await FtpFileSystem.ConnectAsync(entry.Host, entry.Port, credentials.Username, credentials.Password, settings, CancellationToken.None);
                    }

                    return current;
                }
                finally
                {
                    gate.Release();
                }
            },
            Closed = () => current.Dispose(),
        };
        FileManagerWindow.ShowFor(_owner, source, _transfers, _history);
    }

    private RdpCredentials? Login(SessionEntry entry, string? message)
    {
        IReadOnlyList<SavedLogin> saved;
        try
        {
            saved = _logins.List(entry);
        }
        catch (Exception ex)
        {
            Log.Error("FTP: logins salvos", ex);
            saved = [];
        }

        string? Password(string user)
        {
            try
            {
                return _logins.ReadPassword(entry, user);
            }
            catch (Exception ex)
            {
                Log.Error("FTP: ler senha", ex);
                return null;
            }
        }

        var ask = message is not null || saved.Count == 0 || (entry.ChooseLoginOnConnect && saved.Count > 1);
        if (!ask && saved.FirstOrDefault(s => s.IsDefault) is { HasPassword: true } login && Password(login.Username) is { } stored)
        {
            return new RdpCredentials(login.Username, stored, Remember: false);
        }

        return RdpLoginWindow.Ask(_owner, entry, saved, Password, message);
    }

    private void Remember(SessionEntry entry, RdpCredentials credentials)
    {
        var target = _sessions.Document.Sessions.FirstOrDefault(s => s.Id == entry.Id);
        if (target is null || (target.Logins.Contains(credentials.Username, StringComparer.Ordinal) && !credentials.Remember))
        {
            return;
        }

        try
        {
            _logins.Save(target, credentials.Username, credentials.Remember ? credentials.Password : null);
            _sessions.Save();
        }
        catch (Exception ex)
        {
            Log.Error("FTP: salvar login", ex);
        }
    }

    private static string Explain(Exception ex, SessionEntry entry) => ex switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } =>
            $"{entry.Host}:{entry.Port} recusou a conexão (servidor FTP desligado ou porta errada).",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound } =>
            $"O nome {entry.Host} não foi encontrado no DNS.",
        TimeoutException or System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut } =>
            $"{entry.Host} não respondeu (firewall ou endereço errado).",
        System.Security.Authentication.AuthenticationException =>
            "Falha no TLS. Se o servidor não tem FTPS, edite a sessão e escolha \"FTP sem criptografia\" (ou o tipo certo de FTPS).",
        _ => ex.Message,
    };
}
