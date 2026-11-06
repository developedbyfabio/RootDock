using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RootDock.Core.Sftp;

namespace RootDock.App.Views;

/// <summary>
/// Larguras das colunas do painel de arquivos, compartilhadas pelo cabeçalho e por todas as linhas
/// (as linhas ligam nestas propriedades; mudar uma largura atualiza só o que depende dela).
/// </summary>
public sealed class FileColumnsLayout : ObservableObject
{
    private FileColumnSettings _settings = new();
    private Action? _save;
    private double _available;

    public GridLength NameWidth => Length(FileColumn.Name);

    public GridLength SizeWidth => Length(FileColumn.Size);

    public GridLength ModifiedWidth => Length(FileColumn.Modified);

    public GridLength OwnerWidth => Length(FileColumn.Owner);

    public GridLength GroupWidth => Length(FileColumn.Group);

    public GridLength PermissionsWidth => Length(FileColumn.Permissions);

    public Visibility SizeVisibility => Visible(FileColumn.Size);

    public Visibility ModifiedVisibility => Visible(FileColumn.Modified);

    public Visibility OwnerVisibility => Visible(FileColumn.Owner);

    public Visibility GroupVisibility => Visible(FileColumn.Group);

    public Visibility PermissionsVisibility => Visible(FileColumn.Permissions);

    public FileColumnSettings Settings => _settings;

    /// <summary>Nome automático (ocupa o espaço que sobra).</summary>
    public bool IsNameAuto => _settings.NameWidth is null;

    /// <summary>Largura de todas as colunas visíveis.</summary>
    public double TotalWidth => _settings.TotalWidth(_available);

    /// <summary>Usa as preferências salvas; <paramref name="save"/> grava o settings.json.</summary>
    public void Attach(FileColumnSettings settings, Action save)
    {
        settings.Sanitize();
        _settings = settings;
        _save = save;
        OnPropertyChanged(string.Empty);
    }

    public void Save() => _save?.Invoke();

    public double WidthOf(FileColumn column) => _settings.WidthOf(column, _available);

    public bool IsVisible(FileColumn column) => _settings.IsVisible(column);

    /// <summary>Espaço útil da lista: o Nome automático acompanha.</summary>
    public void SetAvailable(double width)
    {
        if (Math.Abs(width - _available) < 0.5)
        {
            return;
        }

        _available = width;
        if (IsNameAuto)
        {
            OnPropertyChanged(nameof(NameWidth));
        }
    }

    public void SetWidth(FileColumn column, double width)
    {
        _settings.SetWidth(column, width);
        Changed(column);
    }

    public void AutoSizeName()
    {
        _settings.AutoSizeName();
        Changed(FileColumn.Name);
    }

    public void SetVisible(FileColumn column, bool visible)
    {
        _settings.SetVisible(column, visible);
        Changed(column);
        OnPropertyChanged(Name(column, "Visibility"));
        Save();
    }

    public void Reset()
    {
        _settings.Reset();
        OnPropertyChanged(string.Empty);
        Save();
    }

    private void Changed(FileColumn column)
    {
        OnPropertyChanged(Name(column, "Width"));
        if (column != FileColumn.Name && IsNameAuto)
        {
            OnPropertyChanged(nameof(NameWidth));
        }
    }

    private static string Name(FileColumn column, string suffix) => column + suffix;

    private GridLength Length(FileColumn column) => new(WidthOf(column));

    private Visibility Visible(FileColumn column) => IsVisible(column) ? Visibility.Visible : Visibility.Collapsed;
}
