using System.Text.RegularExpressions;

namespace RootDock.Core.MultiExec;

/// <summary>
/// Reconhece comandos que podem destruir dados, derrubar o servidor ou cortar o acesso. Rodar
/// um desses em vários servidores ao mesmo tempo exige confirmação digitada. Não é uma
/// barreira de segurança (o usuário tem acesso ao servidor de qualquer forma): é uma trava
/// contra o erro de digitação que vira desastre em 50 máquinas.
/// </summary>
public static partial class CommandSafety
{
    private sealed record Rule(Regex Pattern, string Reason);

    private static readonly Rule[] Rules =
    [
        new(RmRecursive(), "apaga arquivos e pastas de forma recursiva (rm -r)"),
        new(RmRoot(), "rm -rf na raiz, em * ou na pasta pessoal"),
        new(Mkfs(), "formata um disco ou partição (mkfs)"),
        new(DdDisk(), "grava direto num disco (dd of=/dev/...)"),
        new(RedirectToDisk(), "sobrescreve um disco (> /dev/sdX)"),
        new(PowerOff(), "desliga ou reinicia o servidor"),
        new(ForkBomb(), "fork bomb: trava o servidor"),
        new(Chmod777(), "abre permissões recursivamente (chmod -R 777)"),
        new(ChownRecursiveRoot(), "troca o dono de pastas do sistema (chown -R /...)"),
        new(SqlDrop(), "apaga banco, tabela ou esquema (DROP / TRUNCATE)"),
        new(SqlDeleteWithoutWhere(), "DELETE sem WHERE apaga todas as linhas da tabela"),
        new(SqlUpdateWithoutWhere(), "UPDATE sem WHERE altera todas as linhas da tabela"),
        new(Firewall(), "limpa ou desliga o firewall"),
        new(StopAccess(), "para o SSH ou a rede (pode cortar o seu acesso)"),
        new(Accounts(), "remove contas ou troca senhas de usuários"),
        new(KillAll(), "encerra todos os processos"),
        new(GitDestructive(), "git destrutivo (push --force, reset --hard, clean -f)"),
        new(DockerPrune(), "remove containers, imagens ou volumes do Docker"),
        new(CrontabRemove(), "apaga o crontab inteiro (crontab -r)"),
        new(LaravelWipe(), "recria ou esvazia o banco (migrate:fresh/reset/rollback, db:wipe)"),
        new(OverwriteSystemFile(), "sobrescreve arquivo crítico do sistema (/etc/passwd, sudoers, fstab...)"),
        new(PackageRemove(), "remove pacotes do sistema"),
    ];

    /// <summary>Motivos (em português) pelos quais o comando é perigoso; vazio se não parece ser.</summary>
    public static IReadOnlyList<string> Analyze(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return [];
        }

        return Rules.Where(r => r.Pattern.IsMatch(command)).Select(r => r.Reason).Distinct().ToList();
    }

    [GeneratedRegex(@"\brm\b(?:\s+-\S+)*?\s+(?:-[^\s-]*[rR][^\s]*|--recursive)\b")]
    private static partial Regex RmRecursive();

    [GeneratedRegex(@"\brm\s+(?:-[^\s]+\s+)*(?:/\*?|\*|~/?\*?|\$HOME/?\*?)(?:\s|;|&|\||$)")]
    private static partial Regex RmRoot();

    [GeneratedRegex(@"\bmkfs(?:\.\w+)?\b|\bmkswap\b|\bwipefs\b|\bfdisk\b|\bparted\b")]
    private static partial Regex Mkfs();

    [GeneratedRegex(@"\bdd\b[^\n;|&]*\bof=/dev/")]
    private static partial Regex DdDisk();

    [GeneratedRegex(@">\s*/dev/(?:sd|nvme|xvd|vd|hd|mmcblk)")]
    private static partial Regex RedirectToDisk();

    [GeneratedRegex(@"\b(?:shutdown|reboot|halt|poweroff)\b|\binit\s+[06]\b|\bsystemctl\s+(?:reboot|poweroff|halt|kexec)\b")]
    private static partial Regex PowerOff();

    [GeneratedRegex(@":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:")]
    private static partial Regex ForkBomb();

    [GeneratedRegex(@"\bchmod\s+(?:-[^\s]*R[^\s]*\s+)\S*777\b|\bchmod\s+\S*777\s+-R\b")]
    private static partial Regex Chmod777();

    [GeneratedRegex(@"\bchown\s+-[^\s]*R[^\s]*\s+\S+\s+/(?:\s|$|etc|usr|var|bin|lib|boot)")]
    private static partial Regex ChownRecursiveRoot();

    [GeneratedRegex(@"\b(?:DROP\s+(?:DATABASE|TABLE|SCHEMA|USER)|TRUNCATE(?:\s+TABLE)?\s+\w)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlDrop();

    [GeneratedRegex(@"\bDELETE\s+FROM\s+[`""\w.]+\s*(?:;|""|'|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SqlDeleteWithoutWhere();

    [GeneratedRegex(@"\bUPDATE\s+[`""\w.]+\s+SET\b(?![^;]*\bWHERE\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlUpdateWithoutWhere();

    [GeneratedRegex(@"\biptables\s+(?:-F|--flush|-X|-P\s+INPUT\s+DROP)|\bufw\s+(?:disable|reset)\b|\bnft\s+flush\b|\bsystemctl\s+(?:stop|disable)\s+(?:firewalld|ufw)\b")]
    private static partial Regex Firewall();

    [GeneratedRegex(@"\b(?:systemctl|service)\s+(?:stop|disable|mask)\s+(?:sshd?|ssh\.service|network|networking|NetworkManager|systemd-networkd)\b|\bifdown\b|\bip\s+link\s+set\s+\S+\s+down\b")]
    private static partial Regex StopAccess();

    // "passwd outro_usuario" como comando (não "/etc/passwd" num caminho).
    [GeneratedRegex(@"\b(?:userdel|deluser|chpasswd)\b|(?:^|[;&|(]\s*|\bsudo\s+)passwd\s+[^\s|;&]+|\busermod\s+-L\b", RegexOptions.Multiline)]
    private static partial Regex Accounts();

    [GeneratedRegex(@"\bkill\s+-(?:9|KILL)\s+-1\b|\bkillall5?\b|\bpkill\s+-9\s+-u\b")]
    private static partial Regex KillAll();

    [GeneratedRegex(@"\bgit\s+push\b[^\n;]*(?:--force\b|--force-with-lease\b|\s-f\b)|\bgit\s+reset\s+--hard\b|\bgit\s+clean\s+-[^\s]*f")]
    private static partial Regex GitDestructive();

    [GeneratedRegex(@"\bdocker\s+(?:system|volume|image|container|network)\s+prune\b|\bdocker\s+(?:rm|rmi)\s+-f\b|\bdocker\s+compose\s+down\s+(?:-v|--volumes)\b")]
    private static partial Regex DockerPrune();

    [GeneratedRegex(@"\bcrontab\s+(?:-[a-z]*r)\b")]
    private static partial Regex CrontabRemove();

    [GeneratedRegex(@"\bartisan\s+(?:migrate:(?:fresh|reset|rollback|refresh)|db:wipe)\b")]
    private static partial Regex LaravelWipe();

    [GeneratedRegex(@"(?:^|[^>])>\s*/etc/(?:passwd|shadow|group|sudoers|fstab|hosts|ssh/sshd_config)\b")]
    private static partial Regex OverwriteSystemFile();

    [GeneratedRegex(@"\b(?:apt(?:-get)?|aptitude)\s+(?:-y\s+)?(?:remove|purge|autoremove)\b|\b(?:yum|dnf|zypper)\s+(?:-y\s+)?(?:remove|erase)\b|\bapk\s+del\b")]
    private static partial Regex PackageRemove();
}
