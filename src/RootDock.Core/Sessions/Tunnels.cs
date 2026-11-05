using System.Text.Json.Serialization;

namespace RootDock.Core.Sessions;

/// <summary>Tipo de redirecionamento de porta (como -L, -R e -D do ssh).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TunnelKind>))]
public enum TunnelKind
{
    /// <summary>-L: porta neste computador que leva a um serviço alcançado pelo servidor.</summary>
    Local,

    /// <summary>-R: porta no servidor que leva a um serviço alcançado por este computador.</summary>
    Remote,

    /// <summary>-D: proxy SOCKS neste computador que navega pela rede do servidor.</summary>
    Dynamic,
}

/// <summary>Túnel salvo numa sessão.</summary>
public sealed class TunnelDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public TunnelKind Kind { get; set; }

    /// <summary>Nome livre, ex.: "MySQL produção".</summary>
    public string? Name { get; set; }

    /// <summary>Onde a porta escuta: neste computador (Local/Dinâmico) ou no servidor (Remoto).</summary>
    public string BindHost { get; set; } = "127.0.0.1";

    public int BindPort { get; set; }

    /// <summary>Destino (não usado no Dinâmico).</summary>
    public string? TargetHost { get; set; }

    public int TargetPort { get; set; }

    /// <summary>Abre sozinho quando a sessão conecta.</summary>
    public bool AutoStart { get; set; } = true;

    public TunnelDefinition Copy(bool newId = false)
    {
        var copy = (TunnelDefinition)MemberwiseClone();
        if (newId)
        {
            copy.Id = Guid.NewGuid();
        }

        return copy;
    }

    /// <summary>Resumo em uma linha para listas e para a barra de status.</summary>
    public string Describe() => Kind switch
    {
        TunnelKind.Local => $"{BindHost}:{BindPort}  →  {TargetHost}:{TargetPort} (pelo servidor)",
        TunnelKind.Remote => $"servidor {BindHost}:{BindPort}  →  {TargetHost}:{TargetPort} (por este computador)",
        _ => $"SOCKS {BindHost}:{BindPort} (navega pela rede do servidor)",
    };

    public string Title => string.IsNullOrWhiteSpace(Name) ? Describe() : Name!;

    /// <summary>Mensagem de erro para a tela, ou null quando está tudo certo.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BindHost))
        {
            return "Informe o endereço de escuta (ex.: 127.0.0.1).";
        }

        if (BindPort is < 1 or > 65535)
        {
            return "A porta de escuta precisa estar entre 1 e 65535.";
        }

        if (Kind != TunnelKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(TargetHost))
            {
                return "Informe o destino (ex.: 127.0.0.1 ou o nome do servidor de banco).";
            }

            if (TargetPort is < 1 or > 65535)
            {
                return "A porta de destino precisa estar entre 1 e 65535.";
            }
        }

        return null;
    }

    /// <summary>Escuta em todas as interfaces (outros computadores da rede podem usar).</summary>
    public bool IsExposed => BindHost is "0.0.0.0" or "*" or "::";
}
