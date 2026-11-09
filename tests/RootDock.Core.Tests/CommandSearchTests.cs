using RootDock.App.ViewModels;
using RootDock.Core.Commands;

namespace RootDock.Core.Tests;

/// <summary>
/// Busca do painel Comandos: o Enter executa o primeiro resultado, então o primeiro tem de ser o
/// comando sobre o assunto, não um que só cita a palavra de exemplo na descrição.
/// </summary>
public class CommandSearchTests
{
    private static readonly CommandLibrary Library = CommandLibrary.LoadBuiltIn();

    // Mesmo filtro e mesma ordem do painel (CommandsViewModel.Rebuild).
    private static List<string> Search(string family, string query)
    {
        var words = CommandItemViewModel.QueryWords(query);
        return Library.Categories
            .SelectMany(c => c.For(family).Select(d => new CommandItemViewModel(d, c.Name, isCustom: false, family: family)))
            .Where(i => i.Matches(words))
            .OrderByDescending(i => i.Relevance(words))
            .Select(i => i.Id)
            .ToList();
    }

    [Theory]
    [InlineData("mysql", "banco.")]
    [InlineData("MySQL", "banco.")]
    [InlineData("php", "web.php-v")]
    [InlineData("nginx", "web.nginx-")]
    [InlineData("composer", "php.composer-")]
    [InlineData("certificado", "ssl.")]
    [InlineData("fpm", "php.fpm-")]
    [InlineData("redis", "banco.redis-")]
    [InlineData("laravel", "laravel.")]
    [InlineData("git", "git.status")]
    [InlineData("disco", "disco.df")]
    [InlineData("memória", "memoria.free")]
    [InlineData("log nginx", "web.nginx-")]
    public void First_result_is_about_the_subject(string query, string expectedPrefix)
    {
        var results = Search(OsFamily.Rhel, query);

        Assert.NotEmpty(results);
        Assert.StartsWith(expectedPrefix, results[0]);
    }

    [Fact]
    public void Ranking_only_reorders_the_same_matches()
    {
        foreach (var query in new[] { "php", "erro", "usuario", "log", "porta" })
        {
            var words = CommandItemViewModel.QueryWords(query);
            var all = Library.Categories.SelectMany(c => c.For(OsFamily.Debian).Select(d => new CommandItemViewModel(d, c.Name, isCustom: false, family: OsFamily.Debian))).ToList();
            var filtered = all.Where(i => i.Matches(words)).Select(i => i.Id).OrderBy(id => id).ToList();

            Assert.Equal(filtered, Search(OsFamily.Debian, query).OrderBy(id => id).ToList());
        }
    }

    [Fact]
    public void A_mention_only_in_the_description_ranks_last()
    {
        // "processos.user" cita mysql como exemplo de conta; os do MySQL vêm antes.
        var results = Search(OsFamily.Rhel, "mysql");

        Assert.Contains("processos.user", results);
        Assert.All(results.TakeWhile(id => id != "processos.user"), id => Assert.StartsWith("banco.", id));
    }

    [Fact]
    public void Ties_keep_the_catalog_order()
    {
        var item = (string id) => Library.Categories.SelectMany(c => c.For(OsFamily.Rhel).Select(d => new CommandItemViewModel(d, c.Name, false, family: OsFamily.Rhel))).Single(i => i.Id == id);
        var words = CommandItemViewModel.QueryWords("git");

        // Mesmo peso (tema e comando): status vem antes de log, como no catálogo.
        Assert.Equal(item("git.status").Relevance(words), item("git.log").Relevance(words));
        var results = Search(OsFamily.Rhel, "git");
        Assert.True(results.IndexOf("git.status") < results.IndexOf("git.log"));
    }

    [Fact]
    public void Title_match_needs_a_word_start()
    {
        var words = CommandItemViewModel.QueryWords("php");
        var phpVersion = new CommandItemViewModel(new CommandDefinition { Id = "a", Title = "Versão do PHP", Cmd = "x", Description = "d" }, "c", false);
        var inside = new CommandItemViewModel(new CommandDefinition { Id = "b", Title = "Arquivo ophp", Cmd = "x", Description = "d" }, "c", false);

        Assert.True(phpVersion.Relevance(words) > inside.Relevance(words));
    }
}
