using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RootDock.App.Services;
using RootDock.Core.Ftp;
using RootDock.Core.Rdp;
using RootDock.Core.Security;
using RootDock.Core.Serial;
using RootDock.Core.Sessions;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RootDock.App.Dialogs;

/// <summary>Resultado do editor: a sessão alterada e se é para abrir o terminal em seguida.</summary>
public sealed record SessionEditResult(SessionEntry Entry, bool Connect);

/// <summary>
/// Criar/editar sessão: protocolo (SSH, Telnet, Serial), modo Simples (só o essencial) ou
/// Avançado (tudo). Cada protocolo mostra só os campos que fazem sentido para ele.
/// </summary>
public partial class SessionEditorWindow : FluentWindow
{
    private readonly SessionEntry _entry;
    private readonly bool _isNew;
    private readonly bool _hasLogins;
    private readonly List<string> _tags;
    private bool _connect;
    private bool _ready;

    /// <summary>Último modo e protocolo escolhidos em "Nova sessão" (valem enquanto o RootDock está aberto).</summary>
    private static bool _newSessionAdvanced;

    /// <summary>Opção da lista de bastions.</summary>
    private sealed record JumpOption(Guid? Id, string Name);

    private SessionEditorWindow(SessionEntry entry, IEnumerable<string> folders, bool isNew, IReadOnlyList<SessionEntry> sessions)
    {
        InitializeComponent();
        DialogCentering.KeepCentered(this);
        _entry = entry;
        _isNew = isNew;
        _tags = [.. entry.Tags];
        TitleBar.Title = isNew ? "Nova sessão" : "Editar sessão";

        // Nova sessão, como no MobaXterm: Conectar salva na lista e já abre o terminal
        // (sem usuário, o login é pedido no próprio terminal). Ao editar, o padrão é só salvar.
        PrimaryButton.Content = isNew ? "Conectar" : "Salvar";
        SecondaryButton.Content = isNew ? "Só salvar" : "Salvar e conectar";

        NameBox.Text = entry.Name;
        FolderBox.ItemsSource = folders.ToList();
        FolderBox.Text = entry.FolderPath;
        HostBox.Text = entry.Host;
        PortBox.Text = entry.Port.ToString();
        UserBox.Text = entry.Username ?? "";
        KeyBox.Text = entry.PrivateKeyPath ?? "";
        StartupBox.Text = entry.StartupCommand ?? "";
        NotesBox.Text = entry.Notes ?? "";
        EnvironmentBox.SelectedIndex = (int)entry.Environment;

        static IEnumerable<string> Distinct(IEnumerable<string?> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase);
        CompanyBox.ItemsSource = Distinct(sessions.Select(s => s.Company)).ToList();
        BranchBox.ItemsSource = Distinct(sessions.Select(s => s.Branch)).ToList();
        CompanyBox.Text = entry.Company ?? "";
        BranchBox.Text = entry.Branch ?? "";

        var jumps = new List<JumpOption> { new(null, "Nenhum (conexão direta)") };
        jumps.AddRange(sessions
            .Where(s => s.Kind == SessionKind.Ssh && s.Id != entry.Id)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new JumpOption(s.Id, $"{s.Name}  ({s.Endpoint})")));
        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedItem = jumps.FirstOrDefault(j => j.Id == entry.JumpSessionId) ?? jumps[0];
        AgentBox.IsChecked = entry.UseAgent;

        // Serial
        var serial = entry.Serial ?? new SerialSettings();
        var ports = SerialSettings.AvailablePorts();
        SerialPortBox.ItemsSource = ports;
        SerialPortBox.Text = entry.Serial?.PortName ?? ports.FirstOrDefault() ?? "COM1";
        BaudBox.ItemsSource = SerialSettings.CommonBaudRates;
        BaudBox.Text = serial.BaudRate.ToString();
        Select(DataBitsBox, serial.DataBits.ToString());
        Select(ParityBox, serial.Parity);
        Select(StopBitsBox, serial.StopBits);
        Select(FlowBox, serial.FlowControl.ToString());

        // Área de Trabalho Remota
        var rdp = entry.Rdp ?? new RdpSettings();
        RdpResolutionBox.ItemsSource = RdpSettings.CommonResolutions.Select(ResolutionLabel).ToList();
        RdpResolutionBox.Text = ResolutionLabel(rdp.Resolution);
        RdpClipboardBox.IsChecked = rdp.RedirectClipboard;
        RdpDrivesBox.IsChecked = rdp.RedirectDrives;
        RdpPrintersBox.IsChecked = rdp.RedirectPrinters;
        RdpAudioBox.IsChecked = rdp.PlayAudio;
        RdpAdminBox.IsChecked = rdp.AdminSession;
        RdpWindowsKeysBox.IsChecked = rdp.RemoteWindowsKeys;

        // FTP
        var ftp = entry.Ftp ?? new FtpSettings();
        Select(FtpSecurityBox, ftp.Security.ToString());
        FtpPassiveBox.IsChecked = ftp.Passive;

        // Tags
        TagInput.ItemsSource = Distinct(sessions.SelectMany(s => s.Tags)).ToList();
        RenderTags();

        _hasLogins = !isNew && entry.Logins.Count > 0;
        LoginsText.Text = string.Join(", ", entry.Logins.Select(u => u == entry.Username ? $"{u} (padrão)" : u));

        (entry.Kind switch
        {
            SessionKind.Telnet => TelnetKind,
            SessionKind.Serial => SerialKind,
            SessionKind.Rdp => RdpKind,
            SessionKind.Vnc => VncKind,
            SessionKind.Ftp => FtpKind,
            _ => SshKind,
        }).IsChecked = true;
        // Nova sessão abre no modo Simples (ou no último escolhido); editar abre no Avançado.
        ((!isNew || _newSessionAdvanced) ? AdvancedTab : SimpleTab).IsChecked = true;
        _ready = true;
        ApplyLayout();

        Loaded += (_, _) => FirstField().Focus();
    }

    /// <summary>Edita uma cópia da sessão. Retorna null se cancelado.</summary>
    public static SessionEditResult? Edit(
        Window owner, SessionEntry entry, IEnumerable<string> folders, CredentialVault vault, bool isNew, IReadOnlyList<SessionEntry> sessions)
    {
        var copy = entry.Copy();
        var dialog = new SessionEditorWindow(copy, folders, isNew, sessions) { Owner = owner };
        return dialog.ShowDialog() == true ? new SessionEditResult(copy, dialog._connect) : null;
    }

    private SessionKind SelectedKind =>
        TelnetKind.IsChecked == true ? SessionKind.Telnet
        : SerialKind.IsChecked == true ? SessionKind.Serial
        : RdpKind.IsChecked == true ? SessionKind.Rdp
        : VncKind.IsChecked == true ? SessionKind.Vnc
        : FtpKind.IsChecked == true ? SessionKind.Ftp
        : SessionKind.Ssh;

    private const string AutoResolutionLabel = "Automática (acompanha a aba)";

    private static string ResolutionLabel(string resolution) => resolution == RdpSettings.Auto ? AutoResolutionLabel : resolution;

    private bool Advanced => AdvancedTab.IsChecked == true;

    private Control FirstField() =>
        SelectedKind == SessionKind.Serial ? SerialPortBox
        : Advanced && !string.IsNullOrEmpty(HostBox.Text) ? NameBox
        : HostBox;

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // Porta padrão acompanha o protocolo (22, 23, 3389), a menos que já tenha sido mudada à mão.
        if (int.TryParse(PortBox.Text, out var port) && port is 22 or 23 or 3389 or 5900 or 21 or 990)
        {
            PortBox.Text = SelectedKind == SessionKind.Ftp && TagOf(FtpSecurityBox) == "Implicit" ? "990" : SessionEntry.DefaultPort(SelectedKind).ToString();
        }

        ApplyLayout();
    }

    /// <summary>FTPS implícito usa a porta 990; os outros, 21.</summary>
    private void OnFtpSecurityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedKind != SessionKind.Ftp || !int.TryParse(PortBox.Text, out var port) || port is not (21 or 990))
        {
            return;
        }

        PortBox.Text = TagOf(FtpSecurityBox) == "Implicit" ? "990" : "21";
        ApplyLayout();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        if (_isNew)
        {
            _newSessionAdvanced = Advanced;
        }

        ApplyLayout();
    }

    /// <summary>Mostra só os campos do protocolo e do modo escolhidos.</summary>
    private void ApplyLayout()
    {
        var kind = SelectedKind;
        var advanced = Advanced;
        var network = kind is SessionKind.Ssh or SessionKind.Telnet or SessionKind.Rdp or SessionKind.Vnc or SessionKind.Ftp;
        var terminal = kind is SessionKind.Ssh or SessionKind.Telnet;

        Show(NameField, advanced);
        Show(FolderField, advanced);
        Show(HostField, network);
        Show(SerialPortField, kind == SessionKind.Serial);
        Show(SerialDetailsField, kind == SessionKind.Serial && advanced);
        Show(UserField, network && kind != SessionKind.Vnc); // VNC só tem senha
        Show(KeyField, kind == SessionKind.Ssh && advanced);
        Show(EnvironmentField, advanced);
        Show(TagsField, advanced);
        Show(StartupField, terminal && advanced);
        Show(RdpOptionsField, kind == SessionKind.Rdp && advanced);
        Show(FtpOptionsField, kind == SessionKind.Ftp);
        Show(FtpPassiveBox, kind == SessionKind.Ftp && advanced);
        Show(NotesField, advanced);
        Show(LoginsField, advanced && network && _hasLogins);
        Show(AdvancedSeparator, advanced);
        Show(CompanyField, advanced);
        Show(JumpField, kind == SessionKind.Ssh && advanced);
        Show(AgentField, kind == SessionKind.Ssh && advanced);
        Show(TelnetWarning, kind == SessionKind.Telnet || (kind == SessionKind.Ftp && TagOf(FtpSecurityBox) == "None"));
        TelnetWarning.Text = kind == SessionKind.Ftp
            ? "FTP sem criptografia: usuário, senha e arquivos passam em texto puro. Prefira FTPS ou SFTP (SSH)."
            : "Telnet não é criptografado (usuário e senha passam em texto puro): use só em rede confiável.";
        Show(ModeHint, !advanced);

        UserBox.PlaceholderText = kind switch
        {
            SessionKind.Telnet => "Opcional: login automático",
            SessionKind.Rdp => "Ex.: Administrador ou EMPRESA\\joao (vazio = perguntar)",
            SessionKind.Ftp => "Vazio = perguntar ao conectar (ou acesso anônimo)",
            _ => "Vazio = pedir no terminal ao conectar",
        };
        ModeHint.Text = kind switch
        {
            SessionKind.Telnet => "Só o essencial. Se informar o usuário (e salvar a senha em Logins salvos), o RootDock faz o login sozinho.",
            SessionKind.Ftp => "Servidor, usuário e o tipo de segurança. Abre direto no gerenciador de arquivos (computador e servidor lado a lado). A senha é pedida ao conectar e pode ficar salva no Cofre do Windows.",
            SessionKind.Vnc => "Só o servidor (porta padrão 5900; tela :1 = 5901). A senha é pedida ao conectar e pode ficar salva no Cofre do Windows.",
            SessionKind.Rdp => "Área de Trabalho Remota do Windows. Só o essencial: servidor e usuário. A senha é pedida ao conectar (e pode ficar salva no Cofre do Windows). Resolução, copiar e colar, discos e impressoras ficam em Avançado.",
            SessionKind.Serial => "Escolha a porta COM e a velocidade. Consoles de switch e roteador quase sempre usam 9600 (8N1). Formato e controle de fluxo ficam em Avançado.",
            _ => "Só o essencial. Sem usuário, o login é pedido no terminal. Nome, pasta, tags, chave e bastion ficam em Avançado.",
        };
        PrimaryButton.ToolTip = _isNew
            ? kind is SessionKind.Rdp or SessionKind.Vnc or SessionKind.Ftp
                ? kind switch
                {
                    SessionKind.Vnc => "Salva na lista de sessões e abre o VNC numa aba",
                    SessionKind.Ftp => "Salva na lista de sessões e abre o gerenciador de arquivos",
                    _ => "Salva na lista de sessões e abre a Área de Trabalho Remota numa aba",
                }
                : "Salva na lista de sessões e abre o terminal" + (kind == SessionKind.Ssh ? ". Sem usuário, o login é pedido no terminal." : ".")
            : null;
    }

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    // ------------------------------------------------------------------ tags

    private void RenderTags()
    {
        TagChips.Children.Clear();
        foreach (var tag in _tags)
        {
            var chip = TagColors.Chip(tag);
            var remove = new Button
            {
                Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss12, FontSize = 10 },
                Padding = new Thickness(3),
                Margin = new Thickness(6, 0, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = chip.Foreground,
                ToolTip = $"Tirar a tag {tag}",
            };
            remove.Click += (_, _) =>
            {
                _tags.Remove(tag);
                RenderTags();
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = tag, Foreground = chip.Foreground, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(remove);
            TagChips.Children.Add(new Border
            {
                Background = chip.Background,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 2, 3, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Child = content,
            });
        }

        TagChips.Visibility = _tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddTagFromInput()
    {
        if (SessionTags.Clean(TagInput.Text) is { } tag && !_tags.Any(t => SessionTags.Same(t, tag)))
        {
            _tags.Add(tag);
            RenderTags();
        }

        TagInput.Text = "";
        TagInput.Focus();
    }

    private void OnAddTag(object sender, RoutedEventArgs e) => AddTagFromInput();

    private void OnTagKeyDown(object sender, KeyEventArgs e)
    {
        // Enter na caixa de tag adiciona a tag (e não aciona o botão Conectar/Salvar).
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TagInput.Text))
        {
            e.Handled = true;
            AddTagFromInput();
        }
    }

    // ------------------------------------------------------------------ salvar

    private void OnPrimary(object sender, RoutedEventArgs e) => Commit(connect: _isNew);

    private void OnSecondary(object sender, RoutedEventArgs e) => Commit(connect: !_isNew);

    private void Commit(bool connect)
    {
        var kind = SelectedKind;
        if (!string.IsNullOrWhiteSpace(TagInput.Text))
        {
            AddTagFromInput(); // tag digitada e não confirmada também vale
        }

        if (kind == SessionKind.Serial)
        {
            var serial = new SerialSettings
            {
                PortName = SerialPortBox.Text.Trim().ToUpperInvariant(),
                BaudRate = int.TryParse(BaudBox.Text.Trim(), out var baud) ? baud : 0,
                DataBits = int.Parse(TagOf(DataBitsBox)),
                Parity = TagOf(ParityBox),
                StopBits = TagOf(StopBitsBox),
                FlowControl = Enum.Parse<SerialFlowControl>(TagOf(FlowBox)),
            };
            if (serial.Validate() is { } invalid)
            {
                ShowError(invalid);
                SerialPortBox.Focus();
                return;
            }

            _entry.Serial = serial;
            _entry.Host = "";
            _entry.Username = null;
            _entry.Name = NameBox.Text.Trim() is { Length: > 0 } serialName ? serialName : serial.PortName;
        }
        else
        {
            var host = HostBox.Text.Trim();
            if (host.Length == 0)
            {
                ShowError("Informe o servidor (IP ou nome).");
                HostBox.Focus();
                return;
            }

            if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                ShowError("Porta inválida (1 a 65535).");
                PortBox.Focus();
                return;
            }

            var key = kind == SessionKind.Ssh ? KeyBox.Text.Trim() : "";
            if (key.Length > 0 && !File.Exists(key))
            {
                ShowError("Arquivo de chave privada não encontrado.");
                AdvancedTab.IsChecked = true;
                KeyBox.Focus();
                return;
            }

            if (kind == SessionKind.Rdp)
            {
                var resolution = RdpResolutionBox.Text.Trim();
                var rdp = new RdpSettings
                {
                    Resolution = resolution.Length == 0 || resolution == AutoResolutionLabel ? RdpSettings.Auto : resolution.Replace(" ", "").ToLowerInvariant(),
                    RedirectClipboard = RdpClipboardBox.IsChecked == true,
                    RedirectDrives = RdpDrivesBox.IsChecked == true,
                    RedirectPrinters = RdpPrintersBox.IsChecked == true,
                    PlayAudio = RdpAudioBox.IsChecked == true,
                    AdminSession = RdpAdminBox.IsChecked == true,
                    RemoteWindowsKeys = RdpWindowsKeysBox.IsChecked == true,
                };
                if (rdp.Validate() is { } invalidRdp)
                {
                    ShowError(invalidRdp);
                    AdvancedTab.IsChecked = true;
                    RdpResolutionBox.Focus();
                    return;
                }

                _entry.Rdp = rdp;
            }
            else
            {
                _entry.Rdp = null;
            }

            _entry.Ftp = kind == SessionKind.Ftp
                ? new FtpSettings
                {
                    Security = Enum.Parse<FtpSecurity>(TagOf(FtpSecurityBox)),
                    Passive = FtpPassiveBox.IsChecked == true,
                    TrustedCertificate = _entry.Ftp?.TrustedCertificate,
                }
                : null;

            _entry.Serial = null;
            _entry.Host = host;
            _entry.Port = port;
            _entry.Username = kind == SessionKind.Vnc || string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();
            _entry.PrivateKeyPath = key.Length > 0 ? key : null;
            _entry.Name = NameBox.Text.Trim() is { Length: > 0 } name ? name : host;
        }

        _entry.Kind = kind;
        _entry.FolderPath = FolderPaths.Normalize(FolderBox.Text);
        _entry.StartupCommand = kind is SessionKind.Ssh or SessionKind.Telnet && !string.IsNullOrWhiteSpace(StartupBox.Text) ? StartupBox.Text.Trim() : null;
        _entry.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        _entry.Environment = Enum.Parse<SessionEnvironment>(TagOf(EnvironmentBox));
        _entry.Company = string.IsNullOrWhiteSpace(CompanyBox.Text) ? null : CompanyBox.Text.Trim();
        _entry.Branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? null : BranchBox.Text.Trim();
        _entry.Tags = [.. _tags];
        _entry.JumpSessionId = kind == SessionKind.Ssh ? (JumpBox.SelectedItem as JumpOption)?.Id : null;
        _entry.UseAgent = kind == SessionKind.Ssh && AgentBox.IsChecked == true;
        _connect = connect;
        DialogResult = true;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dialog = new OpenFileDialog
        {
            Title = "Escolha a chave privada",
            InitialDirectory = Directory.Exists(sshDir) ? sshDir : null,
            Filter = "Chaves privadas|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            KeyBox.Text = dialog.FileName;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
