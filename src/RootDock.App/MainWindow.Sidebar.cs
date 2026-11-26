using System.Windows;

namespace RootDock.App;

/// <summary>
/// Recolher a barra lateral (Sessões | Arquivos) para o terminal ocupar a largura toda. Recolhida,
/// ela some por inteiro, junto com o divisor. O botão que recolhe e mostra fica sempre no começo
/// da barra de ferramentas, e a barra volta na largura em que estava. O estado é lembrado entre
/// uma abertura e outra do programa.
/// </summary>
public partial class MainWindow
{
    private const double SidebarMinWidth = 180;

    /// <summary>Largura a devolver ao expandir (a coluna fica com zero enquanto recolhida).</summary>
    private double _sidebarExpandedWidth = 280;

    private bool IsSidebarCollapsed => SidebarPanel.Visibility != Visibility.Visible;

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!IsSidebarCollapsed);

    private void SetSidebarCollapsed(bool collapsed, bool save = true)
    {
        if (collapsed == IsSidebarCollapsed)
        {
            return;
        }

        if (collapsed)
        {
            // A largura "de verdade" é a de antes de um alargamento temporário do painel de arquivos.
            var current = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : SidebarColumn.Width.Value;
            _sidebarExpandedWidth = _sidebarBeforeDetails ?? current;
            _sidebarBeforeDetails = null;

            var hadFocus = SidebarPanel.IsKeyboardFocusWithin;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            if (hadFocus)
            {
                _workspace.FocusTerminal(); // o foco não pode ficar num controle que sumiu
            }
        }
        else
        {
            SidebarColumn.MinWidth = SidebarMinWidth;
            SidebarColumn.Width = new GridLength(Math.Clamp(_sidebarExpandedWidth, SidebarMinWidth, SidebarColumn.MaxWidth));
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }

        // O mesmo botão, no mesmo lugar: o ícone e a dica dizem o que o clique faz agora.
        SidebarToggleButton.Icon = new Wpf.Ui.Controls.SymbolIcon(collapsed ? Wpf.Ui.Controls.SymbolRegular.PanelLeftExpand20 : Wpf.Ui.Controls.SymbolRegular.PanelLeftContract20);
        var label = collapsed ? "Mostrar a barra lateral" : "Recolher a barra lateral";
        SidebarToggleButton.ToolTip = label + (collapsed ? ": sessões e arquivos" : "") + " (Ctrl+Shift+B)";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggleButton, label);

        // O espaço mudou: depois do novo arranjo, o assistente volta a caber (ou a ocupar o que escolheram).
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, FitAssistantWidth);

        _settings.SidebarCollapsed = collapsed;
        if (save)
        {
            SaveSettings();
        }
    }

    /// <summary>Largura da barra lateral para guardar nas preferências (a de expandida, mesmo estando recolhida).</summary>
    private double SidebarWidthToRemember() =>
        IsSidebarCollapsed ? _sidebarExpandedWidth : _sidebarBeforeDetails ?? SidebarColumn.ActualWidth;
}
