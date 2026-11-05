using RootDock.App.Dialogs;

namespace RootDock.Core.Tests;

/// <summary>Novidades da versão (Markdown da tag ou dos commits) exibidas como texto na janela de atualizações.</summary>
public class UpdateNotesTests
{
    [Fact]
    public void Markdown_vira_texto_legivel()
    {
        var text = UpdateWindow.PlainNotes("## Novidades\r\n\r\n- **Atualização automática** pelo GitHub\r\n* Ícone no `Sobre`\r\n- Ver [detalhes](https://example.com)\r\n\r\n\r\n\r\nFim");

        Assert.Equal("Novidades\n\n•  Atualização automática pelo GitHub\n•  Ícone no Sobre\n•  Ver detalhes\n\nFim", text.Replace("\r", ""));
    }

    [Fact]
    public void Lista_de_commits_mantem_um_item_por_linha()
    {
        var text = UpdateWindow.PlainNotes("- Corrige o RDP\n- Temas novos");

        Assert.Equal(["•  Corrige o RDP", "•  Temas novos"], text.Replace("\r", "").Split('\n'));
    }
}
