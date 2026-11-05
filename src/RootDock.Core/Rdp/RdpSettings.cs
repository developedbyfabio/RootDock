using System.Text.RegularExpressions;

namespace RootDock.Core.Rdp;

/// <summary>Opções de uma sessão de Área de Trabalho Remota (RDP).</summary>
public sealed partial record RdpSettings
{
    /// <summary>"auto" = acompanha o tamanho da aba; ou uma resolução fixa "1920x1080".</summary>
    public string Resolution { get; init; } = Auto;

    /// <summary>Copiar e colar texto e arquivos entre este computador e o servidor.</summary>
    public bool RedirectClipboard { get; init; } = true;

    /// <summary>Discos deste computador aparecem no servidor (para levar arquivos).</summary>
    public bool RedirectDrives { get; init; }

    /// <summary>Impressoras deste computador aparecem no servidor.</summary>
    public bool RedirectPrinters { get; init; }

    /// <summary>Som do servidor toca neste computador.</summary>
    public bool PlayAudio { get; init; } = true;

    /// <summary>Sessão de console/administração (mstsc /admin).</summary>
    public bool AdminSession { get; init; }

    /// <summary>
    /// Com o foco na Área de Trabalho Remota, as combinações do Windows (tecla Windows, Win+R,
    /// Alt+Tab, Ctrl+Esc) vão para o servidor. Desligado: só em tela cheia (padrão do mstsc).
    /// </summary>
    public bool RemoteWindowsKeys { get; init; } = true;

    public const string Auto = "auto";

    public static readonly string[] CommonResolutions = [Auto, "1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440"];

    /// <summary>Largura e altura fixas, ou null para acompanhar a janela.</summary>
    public (int Width, int Height)? FixedSize =>
        ResolutionPattern().Match(Resolution ?? "") is { Success: true } m
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
            : null;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Resolution) || Resolution == Auto)
        {
            return null;
        }

        if (FixedSize is not { } size)
        {
            return "Resolução inválida: use \"automática\" ou largura x altura (ex.: 1920x1080).";
        }

        return size.Width is < 200 or > 8192 || size.Height is < 200 or > 8192
            ? "Resolução fora do limite (200 a 8192 pixels)."
            : null;
    }

    [GeneratedRegex(@"^\s*(\d{3,4})\s*[xX×]\s*(\d{3,4})\s*$")]
    private static partial Regex ResolutionPattern();
}

/// <summary>Usuário do Windows: "DOMINIO\usuario", "usuario@dominio" ou só "usuario".</summary>
public static class RdpUser
{
    /// <summary>Separa domínio e usuário (o controle RDP recebe os dois em campos próprios).</summary>
    public static (string? Domain, string User) Split(string login)
    {
        login = login.Trim();
        var slash = login.IndexOf('\\');
        if (slash > 0 && slash < login.Length - 1)
        {
            return (login[..slash], login[(slash + 1)..]);
        }

        // usuario@dominio.local vai inteiro (UPN): o Windows entende e o domínio fica implícito.
        return (null, login);
    }
}
