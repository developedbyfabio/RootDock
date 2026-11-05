using System.Text.Json;
using RootDock.App.Services;

namespace RootDock.Core.Tests;

/// <summary>Ícones de arquivo (mesma regra do VS Code com o Material Icon Theme) e temas da interface.</summary>
public sealed class ThemesAndIconsTests
{
    [Theory]
    [InlineData("index.php", "php")]
    [InlineData("pedido.blade.php", "laravel")]
    [InlineData("artisan", "laravel")]
    [InlineData("app.js", "javascript")]
    [InlineData("main.ts", "typescript")]
    [InlineData("estilo.css", "css")]
    [InlineData("tema.scss", "sass")]
    [InlineData("pagina.html", "html")]
    [InlineData("dados.json", "json")]
    [InlineData("README.md", "readme")]
    [InlineData("deploy.sh", "console")]
    [InlineData("docker-compose.yml", "docker")]
    [InlineData("Dockerfile", "docker")]
    [InlineData(".env", "tune")]
    [InlineData(".env.example", "tune")]
    [InlineData(".gitignore", "git")]
    [InlineData(".bashrc", "console")]
    [InlineData("package.json", "nodejs")]
    [InlineData("composer.lock", "json")]
    [InlineData("vite.config.js", "vite")]
    [InlineData("nginx.conf", "nginx")]
    [InlineData("erro.log", "log")]
    [InlineData("backup.tar.gz", "zip")]
    [InlineData("foto.PNG", "image")]
    [InlineData("arquivo.desconhecido123", "file")]
    [InlineData("sem-extensao", "file")]
    public void Files_GetTheSameIconAsVsCode(string name, string expected) =>
        Assert.Equal(expected, FileIconTheme.IconName(name, isDirectory: false));

    [Theory]
    [InlineData("app", "folder-app")]
    [InlineData("routes", "folder-routes")]
    [InlineData("vendor", "folder-lib")]
    [InlineData("node_modules", "folder-node")]
    [InlineData("database", "folder-database")]
    [InlineData(".git", "folder-git")]
    [InlineData("Config", "folder-config")]
    [InlineData("pasta-qualquer", "folder")]
    public void Folders_GetTheSameIconAsVsCode(string name, string expected) =>
        Assert.Equal(expected, FileIconTheme.IconName(name, isDirectory: true));

    [Fact]
    public void LightTheme_UsesLightVariantsWhenTheyExist()
    {
        var light = FileIconTheme.AllIconNames.Where(n => n.EndsWith("_light", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(light);

        // Algum arquivo comum precisa trocar de ícone no tema claro (ícone escuro some no fundo branco).
        string[] candidates = ["x.md", "x.json", "x.lock", "x.toml", "x.astro", "x.ejs", "x.hbs", "x.prisma", "x.sol", "x.zig", "x.cmake", ".npmignore"];
        Assert.Contains(candidates, c => FileIconTheme.IconName(c, false, light: true) != FileIconTheme.IconName(c, false, light: false));
    }

    [Fact]
    public void EveryIcon_IsValidForWpf()
    {
        var problems = FileIconTheme.Validate();
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        Assert.True(FileIconTheme.AllIconNames.Count() > 800);
    }

    [Fact]
    public void Icons_AreBuiltAndShared()
    {
        var php = FileIconTheme.For("index.php", isDirectory: false);
        Assert.NotNull(php);
        Assert.True(php!.IsFrozen);
        Assert.Same(php, FileIconTheme.For("outro.php", isDirectory: false));
        Assert.Equal(php.Width, php.Height); // moldura do viewBox: quadrado
    }

    [Fact]
    public void Themes_HaveUniqueIds_ValidColors_AndKnownTerminalThemes()
    {
        Assert.Equal(AppThemes.All.Count, AppThemes.All.Select(t => t.Id).Distinct().Count());
        foreach (var theme in AppThemes.All)
        {
            foreach (var value in theme.Palette.GetType().GetProperties().Select(p => (string)p.GetValue(theme.Palette)!))
            {
                AppThemes.Parse(value); // lança se a cor for inválida
            }

            foreach (var value in theme.Syntax.GetType().GetProperties().Select(p => (string)p.GetValue(theme.Syntax)!))
            {
                AppThemes.Parse(value);
            }

            Assert.Equal(theme.TerminalTheme, TerminalThemes.Get(theme.TerminalTheme).Id);
        }

        Assert.Contains(AppThemes.All, t => t.IsLight);
        Assert.Contains(AppThemes.All, t => t.Effects.Rain);
        Assert.Contains(AppThemes.All, t => t.Effects.Scanlines);
        Assert.Equal("escuro", AppThemes.Get("tema-que-nao-existe").Id);
    }

    [Fact]
    public void ThemeMessage_CarriesCssVariablesAndEditorColors()
    {
        var json = JsonSerializer.Serialize(AppThemes.PageMessage(AppThemes.Get("matrix")));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("appTheme", root.GetProperty("type").GetString());
        Assert.Equal("#000000", root.GetProperty("vars").GetProperty("--bg").GetString());
        Assert.True(root.GetProperty("effects").GetProperty("rain").GetBoolean());
        Assert.Equal("#00FF41", root.GetProperty("editor").GetProperty("accent").GetString());
        Assert.True(root.GetProperty("editor").GetProperty("syntax").TryGetProperty("string", out _));
    }
}
