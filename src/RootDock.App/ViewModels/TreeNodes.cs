using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RootDock.App.Services;
using RootDock.Core.Sessions;

namespace RootDock.App.ViewModels;

public abstract partial class TreeNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Marcado numa seleção de várias sessões (Ctrl/Shift+clique).</summary>
    [ObservableProperty]
    private bool _isInSelection;

    /// <summary>Destaque enquanto algo é arrastado sobre este item.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public required string Name { get; init; }

    public ObservableCollection<TreeNode> Children { get; } = [];

    /// <summary>Rótulo de seção (não selecionável, não arrastável).</summary>
    public virtual bool IsSection => false;
}

/// <summary>Rótulo "SEM PASTA" antes das sessões que ficam na raiz.</summary>
public sealed class SectionNode : TreeNode
{
    public override bool IsSection => true;
}

public sealed class FolderNode : TreeNode
{
    public required string Path { get; init; }

    public int SessionCount { get; set; }

    /// <summary>Grupo calculado (empresa, filial, ambiente): não é uma pasta de verdade.</summary>
    public bool IsVirtual { get; init; }

    public string CountText => SessionCount > 0 ? SessionCount.ToString() : "";

    public Action<FolderNode, bool>? ExpandedChanged { get; init; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsExpanded))
        {
            ExpandedChanged?.Invoke(this, IsExpanded);
        }
    }
}

public sealed class SessionNode : TreeNode
{
    private static readonly Brush ProdBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0x76, 0x8E)));
    private static readonly Brush StagingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68)));
    private static readonly Brush DevBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0xCE, 0x6A)));
    private static readonly Brush NoneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x70)));

    public required SessionEntry Session { get; init; }

    public string Endpoint => Session.Endpoint;

    /// <summary>Tags da linha: a faixa mostra as que cabem e resume o resto em "+N".</summary>
    public IReadOnlyList<TagChip> TagChips => Session.Tags.Select(TagColors.Chip).ToList();

    /// <summary>Protocolo quando não é SSH (Telnet, Serial), mostrado discretamente na linha.</summary>
    public string KindLabel => Session.Kind switch
    {
        SessionKind.Telnet => "TELNET",
        SessionKind.Serial => "SERIAL",
        SessionKind.Rdp => "RDP",
        SessionKind.Vnc => "VNC",
        SessionKind.Ftp => Session.Ftp?.Security == Core.Ftp.FtpSecurity.None ? "FTP" : "FTPS",
        _ => "",
    };

    public Brush EnvironmentBrush => Session.Environment switch
    {
        SessionEnvironment.Production => ProdBrush,
        SessionEnvironment.Staging => StagingBrush,
        SessionEnvironment.Development => DevBrush,
        _ => NoneBrush,
    };

    public string ToolTip
    {
        get
        {
            var env = Session.Environment switch
            {
                SessionEnvironment.Production => "\nAmbiente: PRODUÇÃO",
                SessionEnvironment.Staging => "\nAmbiente: homologação",
                SessionEnvironment.Development => "\nAmbiente: desenvolvimento",
                _ => "",
            };
            var last = Session.LastConnectedAt is { } at ? $"\nÚltima conexão: {at.LocalDateTime:dd/MM/yyyy HH:mm}" : "";
            var tags = Session.Tags.Count > 0 ? "\nTags: " + string.Join(", ", Session.Tags) : "";
            var company = string.IsNullOrWhiteSpace(Session.Company) ? "" : $"\nEmpresa: {Session.Company}{(string.IsNullOrWhiteSpace(Session.Branch) ? "" : " · " + Session.Branch)}";
            return $"{Session.Endpoint}{env}{company}{tags}{last}";
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
