using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RootDock.App.Services;
using RootDock.Core.Sessions;
using RootDock.Core.Ssh;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>
/// Túneis de uma sessão: criar, editar, remover, abrir e fechar. Os túneis ficam salvos na
/// sessão; com a sessão conectada, abrem na conexão do terminal (sem nova autenticação).
/// </summary>
public partial class TunnelsWindow : FluentWindow
{
    private readonly SessionEntry _entry;
    private readonly SessionManager _sessions;
    private readonly TunnelService _service;
    private TunnelDefinition? _editing;
    private Guid? _confirmRemove;

    private TunnelsWindow(SessionEntry entry, SessionManager sessions, TunnelService service)
    {
        InitializeComponent();
        DialogCentering.KeepCentered(this);
        _entry = entry;
        _sessions = sessions;
        _service = service;
        HeaderText.Text = entry.Name;
        KindBox.SelectedIndex = 0;
        service.Changed += Refresh;
        Closed += (_, _) => service.Changed -= Refresh;
        Refresh();
    }

    public static void ShowFor(Window owner, SessionEntry entry, SessionManager sessions, TunnelService service) =>
        new TunnelsWindow(entry, sessions, service) { Owner = owner }.ShowDialog();

    private TunnelKind SelectedKind =>
        Enum.Parse<TunnelKind>((string)((ComboBoxItem)KindBox.SelectedItem).Tag);

    private void Refresh()
    {
        var connected = _service.IsConnected(_entry.Id);
        ConnectionText.Text = connected
            ? "Sessão conectada: os túneis abrem na conexão do terminal, sem pedir login de novo."
            : "Sessão desconectada: os túneis marcados para abrir sozinhos abrem quando você conectar.";

        Rows.Children.Clear();
        EmptyText.Visibility = _entry.Tunnels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _entry.Tunnels.Count; i++)
        {
            Rows.Children.Add(BuildRow(_entry.Tunnels[i], i == 0, connected));
        }
    }

    private Border BuildRow(TunnelDefinition tunnel, bool first, bool connected)
    {
        var status = _service.Status(_entry.Id, tunnel.Id);
        var (color, label) = status?.State switch
        {
            TunnelState.Running => ("RdDev", "aberto"),
            TunnelState.Starting => ("RdStaging", "abrindo..."),
            TunnelState.Failed => ("RdProd", "falhou"),
            _ => ("RdTextDim", connected ? "fechado" : (tunnel.AutoStart ? "abre ao conectar" : "fechado")),
        };

        var row = new DockPanel { Margin = new Thickness(14, 8, 8, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right);
        if (connected)
        {
            var open = status?.State is TunnelState.Running or TunnelState.Starting;
            actions.Children.Add(SmallButton(open ? "Fechar" : "Abrir", () =>
            {
                if (open)
                {
                    _service.Stop(_entry.Id, tunnel.Id);
                }
                else if (_service.Start(_entry, tunnel) is { } error)
                {
                    ShowError(error);
                }
            }));
        }

        actions.Children.Add(SmallButton("Editar", () => Edit(tunnel)));
        var confirming = _confirmRemove == tunnel.Id;
        var remove = SmallButton(confirming ? "Confirmar remoção" : "Remover", () =>
        {
            if (_confirmRemove != tunnel.Id)
            {
                _confirmRemove = tunnel.Id;
                Refresh();
                return;
            }

            _confirmRemove = null;
            _service.Forget(_entry.Id, tunnel.Id);
            _entry.Tunnels.Remove(tunnel);
            _sessions.Save();
            Refresh();
        });
        if (confirming)
        {
            remove.Appearance = ControlAppearance.Danger;
        }

        actions.Children.Add(remove);
        row.Children.Add(actions);

        var dot = new Ellipse { Width = 8, Height = 8, Fill = (Brush)FindResource(color), Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(dot);

        var info = new StackPanel();
        var title = new TextBlock { Text = tunnel.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new System.Windows.Documents.Run("   " + label)
        {
            FontWeight = FontWeights.Normal,
            FontSize = 11,
            Foreground = (Brush)FindResource(color),
        });
        info.Children.Add(title);
        info.Children.Add(new TextBlock
        {
            Text = (string.IsNullOrWhiteSpace(tunnel.Name) ? "" : tunnel.Describe() + "   ") + KindName(tunnel.Kind),
            FontSize = 11,
            Foreground = (Brush)FindResource("RdTextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (status?.Error is { Length: > 0 } error)
        {
            info.Children.Add(new TextBlock { Text = error, FontSize = 11, Foreground = (Brush)FindResource("RdProd"), TextWrapping = TextWrapping.Wrap });
        }

        row.Children.Add(info);
        return new Border
        {
            Child = row,
            BorderBrush = (Brush)FindResource("RdBorder"),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };
    }

    private static string KindName(TunnelKind kind) => kind switch
    {
        TunnelKind.Local => "local (-L)",
        TunnelKind.Remote => "remoto (-R)",
        _ => "dinâmico / SOCKS (-D)",
    };

    private static Button SmallButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        button.Click += (_, _) => click();
        return button;
    }

    // ------------------------------------------------------------------ formulário

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is null || KindHelp is null)
        {
            return;
        }

        var kind = SelectedKind;
        var dynamic = kind == TunnelKind.Dynamic;
        TargetLabel.Visibility = TargetHostBox.Visibility = TargetColon.Visibility = TargetPortBox.Visibility =
            dynamic ? Visibility.Collapsed : Visibility.Visible;
        BindLabel.Text = kind == TunnelKind.Remote ? "No servidor" : "Neste PC";
        TargetLabel.Text = kind == TunnelKind.Remote ? "Leva até (aqui)" : "Leva até";
        KindHelp.Text = kind switch
        {
            TunnelKind.Local =>
                "Abre uma porta neste computador. O que conectar nela vai, pelo servidor, até o destino. " +
                "Ex.: 127.0.0.1:3307 aqui → 127.0.0.1:3306 (o MySQL do próprio servidor); depois conecte o seu cliente de banco em 127.0.0.1:3307.",
            TunnelKind.Remote =>
                "Abre uma porta no servidor. O que conectar nela vem, por este computador, até o destino. " +
                "Ex.: 127.0.0.1:8080 no servidor → 127.0.0.1:3000 (um app rodando aqui).",
            _ =>
                "Proxy SOCKS neste computador: configure o navegador (ou outro programa) para usar 127.0.0.1 e a porta escolhida " +
                "e ele navega pela rede do servidor.",
        };
    }

    private void OnBindChanged(object sender, TextChangedEventArgs e)
    {
        if (ExposedWarning is not null)
        {
            ExposedWarning.Visibility = new TunnelDefinition { BindHost = BindHostBox.Text.Trim() }.IsExposed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Edit(TunnelDefinition tunnel)
    {
        _editing = tunnel;
        FormTitle.Text = "Editar túnel";
        CancelEditButton.Visibility = Visibility.Visible;
        KindBox.SelectedIndex = (int)tunnel.Kind;
        NameBox.Text = tunnel.Name ?? "";
        BindHostBox.Text = tunnel.BindHost;
        BindPortBox.Text = tunnel.BindPort.ToString();
        TargetHostBox.Text = tunnel.TargetHost ?? "";
        TargetPortBox.Text = tunnel.TargetPort > 0 ? tunnel.TargetPort.ToString() : "";
        AutoStartBox.IsChecked = tunnel.AutoStart;
        BindPortBox.Focus();
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e) => ResetForm();

    private void ResetForm()
    {
        _editing = null;
        FormTitle.Text = "Novo túnel";
        CancelEditButton.Visibility = Visibility.Collapsed;
        NameBox.Text = "";
        BindPortBox.Text = "";
        TargetPortBox.Text = "";
        FormError.Visibility = Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var tunnel = new TunnelDefinition
        {
            Id = _editing?.Id ?? Guid.NewGuid(),
            Kind = SelectedKind,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim(),
            BindHost = BindHostBox.Text.Trim(),
            BindPort = int.TryParse(BindPortBox.Text.Trim(), out var bind) ? bind : 0,
            TargetHost = SelectedKind == TunnelKind.Dynamic ? null : TargetHostBox.Text.Trim(),
            TargetPort = SelectedKind == TunnelKind.Dynamic ? 0 : int.TryParse(TargetPortBox.Text.Trim(), out var target) ? target : 0,
            AutoStart = AutoStartBox.IsChecked == true,
        };
        if (tunnel.Validate() is { } invalid)
        {
            ShowError(invalid);
            return;
        }

        if (_entry.Tunnels.FirstOrDefault(t => t.Id != tunnel.Id && t.Kind != TunnelKind.Remote && tunnel.Kind != TunnelKind.Remote &&
                                              t.BindPort == tunnel.BindPort) is { } clash)
        {
            ShowError($"A porta {tunnel.BindPort} já é usada pelo túnel \"{clash.Title}\" desta sessão.");
            return;
        }

        var index = _entry.Tunnels.FindIndex(t => t.Id == tunnel.Id);
        var wasOpen = _service.Status(_entry.Id, tunnel.Id)?.State is TunnelState.Running or TunnelState.Starting;
        if (index >= 0)
        {
            _service.Forget(_entry.Id, tunnel.Id); // reabre com a configuração nova
            _entry.Tunnels[index] = tunnel;
        }
        else
        {
            _entry.Tunnels.Add(tunnel);
        }

        _sessions.Save();
        if ((wasOpen || tunnel.AutoStart) && _service.IsConnected(_entry.Id))
        {
            // Fechar e abrir na mesma porta: espera o fechamento terminar.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(wasOpen ? 400 : 0);
                if (_service.Start(_entry, tunnel) is { } error)
                {
                    ShowError(error);
                }
            });
        }

        ResetForm();
        Refresh();
    }

    private void ShowError(string message)
    {
        FormError.Text = message;
        FormError.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
