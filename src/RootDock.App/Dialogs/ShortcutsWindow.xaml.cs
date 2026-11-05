using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Todos os atalhos do RootDock, agrupados e com filtro.</summary>
public partial class ShortcutsWindow : FluentWindow
{
    public sealed record Shortcut(string Keys, string Action);

    public sealed record Group(string Title, IReadOnlyList<Shortcut> Items);

    private static readonly Group[] All =
    [
        new("Abas e painéis",
        [
            new("Ctrl+Shift+T", "Novo terminal local"),
            new("Ctrl+Shift+K", "Conexão rápida (IP ou usuario@host:porta)"),
            new("Ctrl+Shift+D", "Dividir à direita"),
            new("Ctrl+Shift+E", "Dividir abaixo"),
            new("Alt+Setas", "Ir para o painel vizinho"),
            new("Ctrl+Tab · Ctrl+PgDn", "Próxima aba"),
            new("Ctrl+Shift+Tab · Ctrl+PgUp", "Aba anterior"),
            new("Ctrl+Shift+W", "Fechar o painel"),
            new("Arrastar a aba", "Juntar na mesma tela (solte numa borda)"),
        ]),
        new("Terminal",
        [
            new("Ctrl+C (com seleção)", "Copiar (sem seleção, interrompe o comando)"),
            new("Ctrl+Shift+C", "Copiar"),
            new("Ctrl+V · Ctrl+Shift+V", "Colar (pede confirmação para várias linhas)"),
            new("Ctrl+Shift+F", "Buscar no terminal"),
            new("Ctrl+Shift+S", "Print do código: imagem bonita da seleção (também no editor)"),
            new("Ctrl+↑ · Ctrl+↓", "Ir para o comando anterior / próximo na tela"),
            new("Ctrl+= / Ctrl+- / Ctrl+0", "Aumentar, diminuir ou voltar o tamanho da fonte"),
            new("Botão direito", "Menu: copiar, colar, buscar, limpar, salvar"),
        ]),
        new("Painéis do RootDock",
        [
            new("Ctrl+Shift+L", "Biblioteca de comandos"),
            new("Ctrl+Shift+H", "Histórico de comandos de todas as sessões"),
            new("Ctrl+F", "Buscar sessões (com a lista em foco)"),
            new("Enter", "Conectar a sessão selecionada"),
            new("F2", "Editar a sessão selecionada"),
            new("Delete", "Excluir as sessões selecionadas"),
            new("Ctrl/Shift+clique", "Selecionar várias sessões"),
        ]),
        new("Gerenciador de arquivos",
        [
            new("F5", "Atualizar"),
            new("F7", "Nova pasta"),
            new("F2", "Renomear"),
            new("Delete", "Excluir (no computador vai para a Lixeira)"),
            new("Backspace", "Pasta acima"),
            new("Enter · dois cliques", "Abrir"),
            new("Arrastar", "Copiar entre o computador e o servidor"),
        ]),
        new("Editor de arquivos",
        [
            new("Ctrl+S", "Salvar no servidor"),
            new("Ctrl+F · Ctrl+H", "Buscar · substituir"),
            new("Ctrl+G", "Ir para a linha"),
            new("Alt+Z", "Quebrar linhas longas"),
            new("Ctrl+W", "Fechar o arquivo (pergunta se não estiver salvo)"),
        ]),
        new("Área de Trabalho Remota",
        [
            new("Ctrl+Alt+End", "Faz o papel do Ctrl+Alt+Del no servidor"),
            new("Ctrl+Alt+Break", "Entrar e sair da tela cheia"),
            new("Win · Win+R · Alt+Tab", "Vão para o servidor (opção da sessão)"),
        ]),
    ];

    private ShortcutsWindow()
    {
        InitializeComponent();
        Groups.ItemsSource = All;
        Loaded += (_, _) => FilterBox.Focus();
    }

    public static void ShowFor(Window owner) => new ShortcutsWindow { Owner = owner }.ShowDialog();

    private void OnFilter(object sender, TextChangedEventArgs e)
    {
        var words = FilterBox.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(Shortcut s) => words.All(w =>
            s.Keys.Contains(w, StringComparison.CurrentCultureIgnoreCase) || s.Action.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        Groups.ItemsSource = words.Length == 0
            ? All
            : All.Select(g => g with { Items = g.Items.Where(Matches).ToList() }).Where(g => g.Items.Count > 0).ToList();
    }
}
