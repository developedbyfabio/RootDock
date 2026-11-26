using RootDock.Core.Assistant;
using Xunit;

namespace RootDock.Core.Tests;

public class ClaudeCodeLauncherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rootdock-launcher-" + Guid.NewGuid().ToString("N"));

    public ClaudeCodeLauncherTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Cmd()
    {
        var path = Path.Combine(_folder, "claude.cmd");
        File.WriteAllText(path, "@echo off");
        return path;
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_folder, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Exe_RunsDirectly()
    {
        var launcher = ClaudeCodeSession.Launcher(@"C:\x\claude.exe");
        Assert.Equal(@"C:\x\claude.exe", launcher.FileName);
        Assert.Empty(launcher.Prefix);
        Assert.False(launcher.ViaShell);
    }

    [Fact]
    public void Npm_CurrentPackage_RunsTheNativeProgram()
    {
        var native = Touch("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        var launcher = ClaudeCodeSession.Launcher(Cmd());
        Assert.Equal(native, launcher.FileName);
        Assert.False(launcher.ViaShell);
    }

    [Fact]
    public void Npm_OldPackage_RunsNodeOnTheScript()
    {
        var script = Touch("node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var launcher = ClaudeCodeSession.Launcher(Cmd());
        Assert.Equal("node.exe", launcher.FileName);
        Assert.Equal([script], launcher.Prefix);
        Assert.False(launcher.ViaShell);
    }

    [Fact]
    public void Shim_PointingAtAProgram_RunsItDirectly()
    {
        var native = Touch("global", "5", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        var path = Path.Combine(_folder, "claude.cmd");
        File.WriteAllText(path, "@SETLOCAL\r\n@IF NOT DEFINED NODE_PATH (\r\n  @SET \"NODE_PATH=%~dp0\\global\\5\\node_modules\"\r\n)\r\n\"%~dp0\\global\\5\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\" %*\r\n");
        var launcher = ClaudeCodeSession.Launcher(path);
        Assert.Equal(native, launcher.FileName);
        Assert.False(launcher.ViaShell);
    }

    [Fact]
    public void Shim_PointingAtAScript_RunsNodeOnIt()
    {
        var script = Touch("lib", "claude code", "cli.mjs");
        var path = Path.Combine(_folder, "claude.cmd");
        File.WriteAllText(path, "@ECHO off\r\nSET dp0=%~dp0\r\n\"%_prog%\"  \"%dp0%\\lib\\claude code\\cli.mjs\" %*\r\n");
        var launcher = ClaudeCodeSession.Launcher(path);
        Assert.Equal("node.exe", launcher.FileName);
        Assert.Equal([script], launcher.Prefix);
        Assert.False(launcher.ViaShell);
    }

    [Fact]
    public void Shim_PointingAtNothingThatExists_GoesThroughTheShell()
    {
        var path = Path.Combine(_folder, "claude.cmd");
        File.WriteAllText(path, "@echo off\r\n\"%~dp0\\sumiu\\claude.exe\" %*\r\n\"C:\\outro lugar\\claude.exe\" %*\r\n");
        Assert.True(ClaudeCodeSession.Launcher(path).ViaShell);
    }

    [Fact]
    public void Npm_UnknownLayout_GoesThroughTheShell()
    {
        var launcher = ClaudeCodeSession.Launcher(Cmd());
        Assert.True(launcher.ViaShell);
        Assert.EndsWith("cmd.exe", launcher.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseModels_ReadsTheListClaudeCodeReports()
    {
        const string line = """
            {"type":"control_response","response":{"subtype":"success","request_id":"models","response":{"commands":[],"models":[
            {"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","supportedEffortLevels":["low","high"]},
            {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5","supportedEffortLevels":["low","medium","high"]},
            {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5"},
            {"value":"opus","displayName":"repetido"},
            {"displayName":"sem valor"},
            {"value":"x"}]}}}
            """;
        var models = ClaudeCodeSession.ParseModels(line.ReplaceLineEndings(""))!;
        Assert.Equal(["default", "opus", "claude-haiku-4-5-20251001", "x"], models.Select(m => m.Value));
        Assert.Equal("Opus 5.5", models[1].Label);
        Assert.Equal("claude-opus-5-5", models[1].Resolved);
        Assert.Equal(["low", "medium", "high"], models[1].Efforts);
        Assert.Empty(models[2].Efforts);
        Assert.Equal("x", models[3].Label);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","model":"claude-opus-5-5"}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"error","request_id":"models","error":"x"}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","response":{"commands":[]}}}""")]
    [InlineData("não é json")]
    [InlineData("")]
    public void ParseModels_IgnoresEverythingElse(string line) => Assert.Null(ClaudeCodeSession.ParseModels(line));

    [Theory]
    [InlineData("-p", "-p")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Users\a b\x.json", "\"C:\\Users\\a b\\x.json\"")]
    [InlineData("mcp__rootdock__*,WebSearch", "\"mcp__rootdock__*,WebSearch\"")]
    [InlineData("a\"b&c\nd", "\"a'b&c d\"")]
    [InlineData(@"C:\Users\R&D\claude.cmd", "\"C:\\Users\\R&D\\claude.cmd\"")]
    [InlineData("50%", "\"50 \"")]
    [InlineData(@"C:\pasta com espaço\", "\"C:\\pasta com espaço\\\\\"")]
    public void ShellQuote_LeavesNothingForCmdToReinterpret(string argument, string expected) =>
        Assert.Equal(expected, ClaudeCodeSession.ShellQuote(argument));
}
