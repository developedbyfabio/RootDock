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
        Assert.All(Library.AllCommands.Where(c => !c.Keys), c =>
        {
            var fields = CommandTemplate.FieldNames(c.Cmd).OrderBy(x => x).ToList();
            var parameters = c.Params.Select(p => p.Name).OrderBy(x => x).ToList();
            Assert.Equal(fields, parameters);
        });
    }

    [Fact]
    public void Library_has_no_destructive_commands()
    {
        foreach (var command in Library.AllCommands)
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
        foreach (var command in Library.AllCommands.Where(c => !c.Keys))
        {
            if (mustConfirm.Any(word => command.Cmd.Contains(word, StringComparison.Ordinal)) && !command.Cmd.StartsWith("systemctl status", StringComparison.Ordinal))
            {
                Assert.True(command.IsCaution, $"{command.Id} altera o servidor e deveria pedir confirmação: {command.Cmd}");
            }
        }
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
