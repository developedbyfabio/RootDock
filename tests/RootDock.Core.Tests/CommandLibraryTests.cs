using System.Text.RegularExpressions;
using RootDock.Core.Commands;

namespace RootDock.Core.Tests;

public class CommandLibraryTests
{
    private static readonly CommandLibrary Library = CommandLibrary.LoadBuiltIn();

    /// <summary>
    /// A biblioteca é usada por quem não conhece Linux: nada destrutivo ou irreversível pode estar nela
    /// (mesma política de segurança da empresa para comandos de banco e git).
    /// </summary>
    private static readonly IReadOnlyList<string> Forbidden = CommandSafety.Patterns;

    [Theory]
    [InlineData("ubuntu debian", OsFamily.Debian)]
    [InlineData("debian", OsFamily.Debian)]
    [InlineData("rocky rhel centos fedora", OsFamily.Rhel)]
    [InlineData("\"rhel\" \"fedora\"", OsFamily.Rhel)]
    [InlineData("ol fedora", OsFamily.Rhel)]
    [InlineData("almalinux rhel centos fedora", OsFamily.Rhel)]
    [InlineData("sles", OsFamily.Suse)]
    [InlineData("sles_sap", OsFamily.Suse)]
    [InlineData("opensuse-leap suse opensuse", OsFamily.Suse)]
    [InlineData("alpine", null)]
    [InlineData("", null)]
    public void Os_release_ids_map_to_a_family(string ids, string? family) =>
        Assert.Equal(family, OsFamily.FromOsRelease(ids));

    [Fact]
    public void Each_family_gets_its_own_package_manager_firewall_and_logs()
    {
        string Cmd(string family, string id) =>
            Library.Categories.SelectMany(c => c.For(family)).Single(c => c.Id == id).Cmd;

        Assert.Equal("sudo apt install {{pacote}}", Cmd(OsFamily.Debian, "pacotes.install"));
        Assert.Equal("sudo dnf install {{pacote}}", Cmd(OsFamily.Rhel, "pacotes.install"));
        Assert.Equal("sudo zypper install {{pacote}}", Cmd(OsFamily.Suse, "pacotes.install"));
        Assert.Contains("ufw", Cmd(OsFamily.Debian, "seguranca.ufw"));
        Assert.Contains("firewall-cmd", Cmd(OsFamily.Rhel, "seguranca.ufw"));
        Assert.Contains("/var/log/secure", Cmd(OsFamily.Rhel, "seguranca.failed"));
        Assert.Contains("/var/log/httpd/", Cmd(OsFamily.Rhel, "web.apache-errors"));

        // Comandos só de uma família e categorias só do Windows.
        Assert.Contains(Library.Categories.SelectMany(c => c.For(OsFamily.Rhel)), c => c.Id == "seguranca.selinux");
        Assert.DoesNotContain(Library.Categories.SelectMany(c => c.For(OsFamily.Debian)), c => c.Id == "seguranca.selinux");
        Assert.DoesNotContain(Library.Categories.SelectMany(c => c.For(OsFamily.Debian)), c => c.Id.StartsWith("win-", StringComparison.Ordinal));
        var windows = Library.Categories.Where(c => c.For(OsFamily.Windows).Count > 0).Select(c => c.Id).ToList();
        Assert.All(windows, id => Assert.StartsWith("win-", id));
        Assert.Contains(Library.Categories.SelectMany(c => c.For(OsFamily.Windows)), c => c.Run && c.Cmd == "ncpa.cpl");
    }

    [Fact]
    public void Windows_values_use_powershell_or_cmd_quoting()
    {
        var port = Library.Categories.SelectMany(c => c.For(OsFamily.Windows)).Single(c => c.Id == "win-rede.port");
        var detail = Library.Categories.SelectMany(c => c.For(OsFamily.Windows)).Single(c => c.Id == "win-usuarios.detail");

        Assert.Equal("Test-NetConnection srv01 -Port 443",
            CommandTemplate.Render(port, new Dictionary<string, string> { ["host"] = "srv01", ["porta"] = "443" }, windows: true));
        Assert.Equal("Test-NetConnection 'meu servidor' -Port 443",
            CommandTemplate.Render(port, new Dictionary<string, string> { ["host"] = "meu servidor", ["porta"] = "443" }, windows: true));
        Assert.Equal("net user \"joao silva\"",
            CommandTemplate.Render(detail, new Dictionary<string, string> { ["usuario"] = "joao silva" }, windows: true));
        Assert.Throws<CommandRenderException>(() =>
            CommandTemplate.Render(detail, new Dictionary<string, string> { ["usuario"] = "%USERNAME%" }, windows: true));
    }

    [Fact]
    public void Built_in_library_loads_with_categories_and_commands()
    {
        Assert.True(Library.Categories.Count >= 15);
        Assert.True(Library.AllCommands.Count() >= 120);
        Assert.All(Library.Categories, c => Assert.NotEmpty(c.Commands));
    }

    [Fact]
    public void Ids_are_unique_and_every_command_is_described()
    {
        var ids = Library.AllCommands.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Library.AllCommands, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Cmd), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Id);
        });
    }

    [Fact]
    public void Every_field_in_a_template_has_a_parameter_and_vice_versa()
    {
        Assert.All(Library.AllResolved().Select(r => r.Command).Where(c => !c.Keys), c =>
        {
            var fields = CommandTemplate.FieldNames(c.Cmd).OrderBy(x => x).ToList();
            var parameters = c.Params.Select(p => p.Name).OrderBy(x => x).ToList();
            Assert.Equal(fields, parameters);
        });
    }

    [Fact]
    public void Library_has_no_destructive_commands()
    {
        foreach (var (family, command) in Library.AllResolved())
        {
            foreach (var pattern in Forbidden)
            {
                Assert.False(Regex.IsMatch(command.Cmd, pattern, RegexOptions.IgnoreCase),
                    $"{command.Id} contém um comando proibido ({pattern}): {command.Cmd}");
            }
        }
    }

    [Theory]
    [InlineData("sudo reboot")]
    [InlineData("reboot")]
    [InlineData("df -h && shutdown -h now")]
    [InlineData("systemctl poweroff")]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("git reset --hard")]
    [InlineData("php artisan migrate:fresh")]
    [InlineData("docker compose down")]
    [InlineData("UPDATE users SET admin = 1")]
    public void Guard_catches_destructive_commands(string command) =>
        Assert.True(CommandSafety.LooksDestructive(command), command);

    [Theory]
    [InlineData("cat /var/run/reboot-required")]
    [InlineData("df -h")]
    [InlineData("git status")]
    [InlineData("UPDATE users SET admin = 1 WHERE id = 5")]
    public void Guard_allows_harmless_commands(string command) =>
        Assert.False(CommandSafety.LooksDestructive(command), command);

    [Fact]
    public void Commands_that_change_things_are_marked_as_caution()
    {
        var mustConfirm = new[] { "restart", "stop", "kill", "install", "upgrade", "chmod", "chown", "pull", "up -d", "sudo -i", "optimize:clear" };
        foreach (var command in Library.AllResolved().Select(r => r.Command).Where(c => !c.Keys))
        {
            // Só consultas: "systemctl status", "needs-restarting" (RHEL: diz se precisa reiniciar) e a
            // chave Uninstall do Registro (Windows: lista os programas instalados).
            var readOnly = command.Cmd.StartsWith("systemctl status", StringComparison.Ordinal) || command.Cmd.Contains("needs-restarting", StringComparison.Ordinal) ||
                           command.Cmd.StartsWith(@"Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.Ordinal);
            if (mustConfirm.Any(word => command.Cmd.Contains(word, StringComparison.Ordinal)) && !readOnly)
            {
                Assert.True(command.IsCaution, $"{command.Id} altera o servidor e deveria pedir confirmação: {command.Cmd}");
            }
        }
    }

    [Fact]
    public void Category_icons_exist_in_the_icon_set()
    {
        // Nome errado não quebra o painel (vira um ícone genérico), então só um teste pega.
        Assert.All(Library.Categories, c =>
            Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(c.Icon, out _), $"{c.Id}: ícone {c.Icon} não existe"));
    }

    [Fact]
    public void Database_commands_ask_for_the_password_and_never_carry_it()
    {
        // Política da empresa: nada de senha no comando (ficaria no histórico do shell).
        var database = Library.AllResolved().Select(r => r.Command)
            .Where(c => Regex.IsMatch(c.Cmd, @"\b(mysql|mysqldump|mysqladmin|redis-cli)\b")).ToList();

        Assert.NotEmpty(database);
        Assert.All(database, c =>
        {
            Assert.DoesNotMatch(@"\s-p\S|--password|\s-a\s|REDISCLI_AUTH", c.Cmd);
            if (Regex.IsMatch(c.Cmd, @"\b(mysql|mysqldump|mysqladmin)\s+-u\b"))
            {
                Assert.Matches(@"\s-p(\s|$)", c.Cmd);
            }
        });
    }

    [Fact]
    public void Commands_that_may_ask_for_a_password_are_not_piped_into_a_pager()
    {
        // O less disputa o teclado com a pergunta de senha (sudo, mysql -p) e a tela embaralha.
        Assert.All(Library.AllResolved().Select(r => r.Command), c =>
            Assert.False(Regex.IsMatch(c.Cmd, @"(\bsudo\b|\s-p(\s|$)).*\|\s*(less|more)\b"), $"{c.Id}: {c.Cmd}"));
    }

    [Fact]
    public void Php_and_web_commands_follow_each_family()
    {
        CommandDefinition? Resolve(string family, string id) =>
            Library.Categories.SelectMany(c => c.For(family)).SingleOrDefault(c => c.Id == id);

        Assert.Equal("sudo tail -n 100 /var/log/php-fpm/error.log", Resolve(OsFamily.Rhel, "php.fpm-errors")!.Cmd);
        Assert.Equal("sudo tail -n 100 /var/log/php*-fpm.log", Resolve(OsFamily.Debian, "php.fpm-errors")!.Cmd);
        Assert.NotNull(Resolve(OsFamily.Rhel, "php.site-errors"));
        Assert.Null(Resolve(OsFamily.Debian, "php.site-errors"));
        Assert.Contains("reload httpd", Resolve(OsFamily.Rhel, "web.apache-reload")!.Cmd);
        Assert.Contains("reload apache2", Resolve(OsFamily.Suse, "web.apache-reload")!.Cmd);

        // Só o exemplo do campo muda por família; o comando é o mesmo.
        var owner = (Debian: Resolve(OsFamily.Debian, "laravel.fix-owner")!, Rhel: Resolve(OsFamily.Rhel, "laravel.fix-owner")!);
        Assert.Equal(owner.Debian.Cmd, owner.Rhel.Cmd);
        Assert.Equal("www-data", owner.Debian.Param("usuario")!.Placeholder);
        Assert.Equal("apache", owner.Rhel.Param("usuario")!.Placeholder);
        Assert.Equal("sudo chown -R apache: storage bootstrap/cache",
            CommandTemplate.Render(owner.Rhel, new Dictionary<string, string> { ["usuario"] = "apache" }));
    }

    [Fact]
    public void Docker_go_templates_are_not_taken_as_fields()
    {
        var table = Library.AllCommands.Single(c => c.Id == "docker.ps-table");

        Assert.Empty(CommandTemplate.FieldNames(table.Cmd));
        Assert.Equal(table.Cmd, CommandTemplate.Render(table, new Dictionary<string, string>()));
    }

    [Fact]
    public void Tmux_shortcuts_send_ctrl_b_sequences()
    {
        var detach = Library.AllCommands.Single(c => c.Id == "tmux.detach");
        Assert.True(detach.Keys);
        Assert.Equal("\u0002d", detach.Cmd);
        Assert.Equal("Ctrl+B  D", detach.DisplayCommand);
    }

    [Fact]
    public void Render_quotes_unsafe_values_and_keeps_plain_ones_readable()
    {
        var find = Library.AllCommands.Single(c => c.Id == "basico.find");

        var rendered = CommandTemplate.Render(find, new Dictionary<string, string> { ["pasta"] = "/var/log", ["nome"] = "*.log" });

        Assert.Equal("find /var/log -iname '*.log' 2>/dev/null | head -n 50", rendered);
        Assert.Equal("'meu arquivo; rm x'", CommandTemplate.Quote("meu arquivo; rm x"));
        Assert.Equal("'it'\\''s'", CommandTemplate.Quote("it's"));
        Assert.Equal("~/site", CommandTemplate.Quote("~/site"));
    }

    [Fact]
    public void Render_validates_numbers_options_and_empty_fields()
    {
        var kill = Library.AllCommands.Single(c => c.Id == "processos.kill");
        var cd = Library.AllCommands.Single(c => c.Id == "basico.cd");

        Assert.Equal("kill 1234", CommandTemplate.Render(kill, new Dictionary<string, string> { ["pid"] = "1234" }));
        Assert.Throws<CommandRenderException>(() => CommandTemplate.Render(kill, new Dictionary<string, string> { ["pid"] = "1234; reboot" }));
        Assert.Throws<CommandRenderException>(() => CommandTemplate.Render(cd, new Dictionary<string, string> { ["pasta"] = "--help" }));
        Assert.Throws<CommandRenderException>(() => CommandTemplate.Render(cd, new Dictionary<string, string> { ["pasta"] = "  " }));
    }

    [Fact]
    public void Display_command_shows_field_labels()
    {
        var tail = Library.AllCommands.Single(c => c.Id == "logs.follow");

        Assert.Equal("tail -f <arquivo>", tail.DisplayCommand);
    }
}
