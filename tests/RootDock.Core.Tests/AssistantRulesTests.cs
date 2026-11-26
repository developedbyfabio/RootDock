using System.Text.Json;
using RootDock.Core.Assistant;
using Xunit;

namespace RootDock.Core.Tests;

public class AssistantRulesTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ParseBlocked_SkipsBlankLinesCommentsAndRepeats()
    {
        var patterns = AssistantRules.ParseBlocked("rm -rf /\r\n\r\n# comentário\n  shutdown  \nSHUTDOWN\n/\\bmkfs\\b/");
        Assert.Equal(["rm -rf /", "shutdown", "/\\bmkfs\\b/"], patterns);
    }

    [Theory]
    [InlineData("command", "sudo  rm   -rf  /var/x", "rm -rf /")]
    [InlineData("command", "systemctl restart x && SHUTDOWN -h now", "shutdown")]
    [InlineData("text", "mkfs.ext4 /dev/sdb1", "/\\bmkfs\\b/")]
    [InlineData("path", "/etc/shadow", "/etc/shadow")]
    public void Blocked_FindsThePatternInCommandTextOrPath(string field, string value, string expected)
    {
        string[] patterns = ["rm -rf /", "shutdown", "/\\bmkfs\\b/", "/etc/shadow"];
        Assert.Equal(expected, AssistantRules.Blocked(patterns, Json(new Dictionary<string, string> { [field] = value })));
    }

    [Fact]
    public void Blocked_LetsEverythingElseThrough()
    {
        string[] patterns = ["rm -rf /", "/\\bmkfs\\b/", "/[invalida/"];
        Assert.Null(AssistantRules.Blocked(patterns, Json(new { command = "ls -la && df -h" })));
        Assert.Null(AssistantRules.Blocked(patterns, Json(new { command = "echo unmkfsx" })));
        Assert.Null(AssistantRules.Blocked(patterns, Json(new { content = "rm -rf / dentro do conteúdo de um arquivo não é comando" })));
        Assert.Null(AssistantRules.Blocked([], Json(new { command = "rm -rf /" })));
        Assert.Equal("/[invalida/", AssistantRules.Blocked(patterns, Json(new { command = "x /[invalida/ y" }))); // inválida vale como texto
    }

    // Os mesmos padrões dos modelos prontos da tela (assistant.js): o que devem pegar e o que não.
    [Theory]
    [InlineData(@"/\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+\/(\s|$|\*)/", "sudo rm -rf / --no-preserve-root", true)]
    [InlineData(@"/\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+\/(\s|$|\*)/", "rm -fr /*", true)]
    [InlineData(@"/\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+\/(\s|$|\*)/", "rm -rf /", true)]
    [InlineData(@"/\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+\/(\s|$|\*)/", "rm -rf /var/www/app/cache", false)]
    [InlineData(@"/\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+\/(\s|$|\*)/", "rm arquivo.txt", false)]
    [InlineData(@"/\bdd\b.*\bof=\/dev\//", "dd if=/dev/zero of=/dev/sda bs=1M", true)]
    [InlineData(@"/\bdd\b.*\bof=\/dev\//", "dd if=/dev/sda of=/root/disco.img", false)]
    [InlineData(@"/\binit\s+[06]\b/", "init 0", true)]
    [InlineData(@"/\binit\s+[06]\b/", "git init 0x", false)]
    [InlineData(@"/\bmkfs/", "mkfs.ext4 /dev/sdb1", true)]
    [InlineData(@"/\bDROP\s+(DATABASE|SCHEMA)\b/", "mysql -e 'drop database loja'", true)]
    [InlineData(@"/\bDROP\s+(DATABASE|SCHEMA)\b/", "mysql -e 'drop table x'", false)]
    [InlineData(@"/\bgit\s+clean\s+-[a-z]*f/", "git clean -xfd", true)]
    [InlineData(@"/\bgit\s+clean\s+-[a-z]*f/", "git clean -n", false)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"shutdown -h now", true)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"sudo reboot", true)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"systemctl restart nginx && poweroff", true)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"cd /tmp; halt", true)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"last reboot | head", false)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"journalctl -b | grep -i shutdown", false)]
    [InlineData(@"/(^|[;&|(]\s*|\bsudo\s+)(shutdown|reboot|poweroff|halt)\b/", @"grep asphalt x", false)]
    [InlineData(@"/\bsystemctl\s+(poweroff|reboot|halt|kexec)\b/", @"systemctl reboot", true)]
    [InlineData(@"/\bsystemctl\s+(poweroff|reboot|halt|kexec)\b/", @"systemctl status systemd-reboot.service", false)]
    [InlineData(@"/\bTRUNCATE\s+(TABLE\s+)?[\w`""\[]/", @"psql -c 'truncate pedidos'", true)]
    [InlineData(@"/\bTRUNCATE\s+(TABLE\s+)?[\w`""\[]/", @"mysql -e ""TRUNCATE TABLE `log`""", true)]
    [InlineData(@"/\bTRUNCATE\s+(TABLE\s+)?[\w`""\[]/", @"truncate -s 0 /var/log/app.log", false)]
    public void PresetPatterns_CatchWhatTheyClaim(string pattern, string command, bool blocked) =>
        Assert.Equal(blocked, AssistantRules.Blocked([pattern], Json(new { command })) is not null);

    [Fact]
    public void Section_IsEmptyWithoutRules_AndCarriesEachPartOtherwise()
    {
        Assert.Equal("", AssistantRules.Section(" ", "\n", "srv", []));
        var text = AssistantRules.Section("- Nunca mexa em produção sem plano.", "- O projeto fica em /srv/app.", "Loja", ["shutdown"]);
        Assert.Contains("obrigatórias", text);
        Assert.Contains("Nunca mexa em produção sem plano.", text);
        Assert.Contains("deste servidor (Loja)", text);
        Assert.Contains("/srv/app", text);
        Assert.Contains("shutdown", text);
    }

    [Fact]
    public async Task Runner_RefusesABlockedCommand_WithoutAskingOrRunning()
    {
        var observer = new Watcher();
        var runner = new AssistantToolRunner(new AssistantTools(new ThrowingHost()), observer) { Mode = AssistantMode.Auto, Blocked = () => ["shutdown"] };
        var result = await runner.RunAsync(new ToolCall("c1", AssistantTools.RunCommand, Json(new { command = "sudo shutdown -h now" })), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Bloqueado pelas regras", result.Content);
        Assert.Equal(0, observer.Asked);
        Assert.Equal(1, observer.Finished);
    }

    private sealed class Watcher : IAssistantObserver
    {
        public int Asked { get; private set; }

        public int Finished { get; private set; }

        public void Text(string delta)
        {
        }

        public void ToolStarted(ToolCall call, string summary)
        {
        }

        public void ToolFinished(ToolCall call, ToolResult result) => Finished++;

        public Task<bool> ConfirmAsync(ToolCall call, string summary, PermissionCheck check, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(true);
        }

        public void Note(string text)
        {
        }
    }

    /// <summary>Qualquer uso do servidor falha o teste: comando bloqueado não pode chegar aqui.</summary>
    private sealed class ThrowingHost : IAssistantHost
    {
        public string Label => "teste";

        public string? TerminalDirectory => null;

        public Task<MultiExec.CommandResult> RunAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct) => throw new InvalidOperationException("rodou");

        public Task<MultiExec.CommandResult> QueryAsync(string command, TimeSpan timeout, CancellationToken ct) => throw new InvalidOperationException("rodou");

        public void Remember(string note) => throw new InvalidOperationException("rodou");

        public string ReadTerminal(int lines) => throw new InvalidOperationException("rodou");

        public void TypeInTerminal(string text) => throw new InvalidOperationException("rodou");

        public Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken ct) => throw new InvalidOperationException("rodou");

        public Task WriteFileAsync(string path, byte[] content, CancellationToken ct) => throw new InvalidOperationException("rodou");

        public Task<bool> ExistsAsync(string path, CancellationToken ct) => throw new InvalidOperationException("rodou");

        public Task<IReadOnlyList<AssistantFileEntry>> ListAsync(string path, CancellationToken ct) => throw new InvalidOperationException("rodou");
    }
}
