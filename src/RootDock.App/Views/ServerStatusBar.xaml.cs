using System.Windows;
using System.Windows.Controls;

namespace RootDock.App.Views;

public partial class ServerStatusBar : UserControl
{
    public ServerStatusBar()
    {
        InitializeComponent();
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e) => DetailsPopup.IsOpen = !DetailsPopup.IsOpen;
}
