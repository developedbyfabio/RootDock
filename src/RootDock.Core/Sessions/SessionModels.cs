using System.Text.Json.Serialization;

namespace RootDock.Core.Sessions;

[JsonConverter(typeof(JsonStringEnumConverter<SessionKind>))]
public enum SessionKind
{
    Ssh,
    LocalShell,

    /// <summary>Telnet (equipamentos e sistemas legados; sem criptografia).</summary>
    Telnet,

    /// <summary>Console serial (COM).</summary>
    Serial,

    /// <summary>Área de Trabalho Remota do Windows (RDP), numa aba do RootDock.</summary>
    Rdp,

    /// <summary>VNC (acesso gráfico a Linux, Mac, equipamentos), numa aba do RootDock.</summary>
    Vnc,

    /// <summary>FTP ou FTPS: abre direto no gerenciador de arquivos (sem terminal).</summary>
    Ftp,
}

/// <summary>Ambiente do servidor. Produção ganha destaque visual e confirmação de comandos perigosos.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionEnvironment>))]
public enum SessionEnvironment
{
    None,
    Development,
    Staging,
    Production,
}

[JsonConverter(typeof(JsonStringEnumConverter<LocalShellKind>))]
public enum LocalShellKind
{
    PowerShell,
    Pwsh,
    Cmd,
    Wsl,
    GitBash,
}

public sealed class SessionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Caminho da pasta com "/" como separador. Vazio = raiz.</summary>
    public string FolderPath { get; set; } = "";

    public SessionKind Kind { get; set; } = SessionKind.Ssh;

    public string Host { get; set; } = "";

    public int Port { get; set; } = 22;

    /// <summary>Usuário padrão. Vazio = perguntar ao conectar.</summary>
    public string? Username { get; set; }

    /// <summary>Usuários com login salvo (as senhas ficam no cofre; ver SessionLogins).</summary>
    public List<string> Logins { get; set; } = [];

    /// <summary>Com logins salvos, pergunta no terminal qual usar ao abrir.</summary>
    public bool ChooseLoginOnConnect { get; set; } = true;

    /// <summary>Também tenta as chaves do agente SSH (ssh-agent do Windows ou Pageant).</summary>
    public bool UseAgent { get; set; }

    /// <summary>Sessão usada como bastion (ProxyJump). O bastion pode ter o seu próprio bastion.</summary>
    public Guid? JumpSessionId { get; set; }

    /// <summary>Túneis (redirecionamento de portas) desta sessão.</summary>
    public List<TunnelDefinition> Tunnels { get; set; } = [];

    /// <summary>Configuração da porta COM (sessões seriais).</summary>
    public Serial.SerialSettings? Serial { get; set; }

    /// <summary>Opções da Área de Trabalho Remota (só para <see cref="SessionKind.Rdp"/>).</summary>
    public Rdp.RdpSettings? Rdp { get; set; }

    /// <summary>Opções de FTP/FTPS (só para <see cref="SessionKind.Ftp"/>).</summary>
    public RootDock.Core.Ftp.FtpSettings? Ftp { get; set; }

    /// <summary>Porta padrão de cada protocolo.</summary>
    public static int DefaultPort(SessionKind kind) => kind switch
    {
        SessionKind.Telnet => 23,
        SessionKind.Rdp => 3389,
        SessionKind.Vnc => 5900,
        SessionKind.Ftp => 21,
        _ => 22,
    };

    /// <summary>Etiquetas livres (ex.: PRODUÇÃO, BANCO, CRÍTICO): aparecem na lista e servem para buscar e agrupar.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Empresa / cliente (agrupamento da lista).</summary>
    public string? Company { get; set; }

    /// <summary>Filial / unidade / site (agrupamento da lista).</summary>
    public string? Branch { get; set; }

    public string? PrivateKeyPath { get; set; }

    public LocalShellKind? LocalShell { get; set; }

    public SessionEnvironment Environment { get; set; }

    /// <summary>Cor da aba em hexadecimal (#RRGGBB). Opcional.</summary>
    public string? Color { get; set; }

    /// <summary>Comando executado logo após abrir o terminal.</summary>
    public string? StartupCommand { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>Origem da sessão quando importada (ex.: "MobaXterm").</summary>
    public string? ImportedFrom { get; set; }

    /// <summary>Cópia com o mesmo Id (para editar sem alterar o original até salvar).</summary>
    public SessionEntry Copy()
    {
        var copy = (SessionEntry)MemberwiseClone();
        copy.Logins = [.. Logins];
        copy.Tags = [.. Tags];
        copy.Tunnels = Tunnels.Select(t => t.Copy()).ToList();
        return copy;
    }

    /// <summary>Nova sessão igual a esta, com outro Id (duplicar).</summary>
    public SessionEntry Clone()
    {
        var copy = (SessionEntry)MemberwiseClone();
        copy.Logins = [.. Logins];
        copy.Tags = [.. Tags];
        copy.Tunnels = Tunnels.Select(t => t.Copy(newId: true)).ToList();
        copy.Id = Guid.NewGuid();
        copy.LastConnectedAt = null;
        return copy;
    }

    /// <summary>Descrição curta para listas e títulos: usuario@host:porta.</summary>
    public string Endpoint
    {
        get
        {
            if (Kind == SessionKind.LocalShell)
            {
                return LocalShell?.ToString() ?? "Terminal local";
            }

            if (Kind == SessionKind.Serial)
            {
                return Serial?.Describe() ?? "Serial";
            }

            var user = string.IsNullOrWhiteSpace(Username) ? "" : Username + "@";
            var port = Port == DefaultPort(Kind) ? "" : ":" + Port;
            var scheme = Kind switch
            {
                SessionKind.Telnet => "telnet://",
                SessionKind.Rdp => "rdp://",
                SessionKind.Vnc => "vnc://",
                SessionKind.Ftp => Ftp?.Security == RootDock.Core.Ftp.FtpSecurity.None ? "ftp://" : "ftps://",
                _ => "",
            };
            return scheme + user + Host + port;
        }
    }
}

public sealed class SessionDocument
{
    public int Version { get; set; } = 1;

    /// <summary>Pastas explícitas (inclui pastas vazias).</summary>
    public List<string> Folders { get; set; } = [];

    public List<SessionEntry> Sessions { get; set; } = [];

    /// <summary>Todas as pastas: as explícitas e as usadas por sessões, incluindo os ancestrais.</summary>
    public IReadOnlyList<string> AllFolders()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders.Concat(Sessions.Select(s => s.FolderPath)))
        {
            var normalized = FolderPaths.Normalize(folder);
            while (normalized.Length > 0)
            {
                set.Add(normalized);
                normalized = FolderPaths.Parent(normalized);
            }
        }

        return set.ToList();
    }
}

public static class FolderPaths
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts);
    }

    public static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    public static string LeafName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }
}
