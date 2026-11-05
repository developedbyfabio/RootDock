using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RootDock.App.Services;

namespace RootDock.App.Views;

/// <summary>
/// Faixa de tags de uma linha da lista: mostra só as etiquetas que cabem inteiras e resume o resto
/// em "+N" (nunca corta uma etiqueta no meio). Todas aparecem na dica da linha.
/// </summary>
public sealed class TagStrip : Panel
{
    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags), typeof(IReadOnlyList<TagChip>), typeof(TagStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((TagStrip)d).Rebuild()));

    private const double Gap = 4;
    private readonly TextBlock _more = new() { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
    private int _visible;

    public TagStrip()
    {
        _more.SetResourceReference(TextBlock.ForegroundProperty, "RdTextDim");
    }

    public IReadOnlyList<TagChip>? Tags
    {
        get => (IReadOnlyList<TagChip>?)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var chip in Tags ?? [])
        {
            Children.Add(new Border
            {
                Background = chip.Background,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = chip.Name, Foreground = chip.Foreground, FontSize = 10.5, FontWeight = FontWeights.SemiBold },
            });
        }

        Children.Add(_more);
    }

    protected override Size MeasureOverride(Size available)
    {
        var chips = Children.Count - 1;
        var widths = new double[chips];
        var height = 0.0;
        for (var i = 0; i < chips; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, available.Height));
            widths[i] = Children[i].DesiredSize.Width;
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        // Quantas cabem inteiras, reservando lugar para o "+N" quando sobrar alguma.
        _visible = 0;
        var used = 0.0;
        for (var k = chips; k >= 0; k--)
        {
            var total = widths.Take(k).Sum() + Gap * Math.Max(0, k - 1);
            if (k < chips)
            {
                _more.Text = $"+{chips - k}";
                _more.Measure(new Size(double.PositiveInfinity, available.Height));
                total += (k > 0 ? Gap : 0) + _more.DesiredSize.Width;
            }

            if (total <= available.Width || k == 0)
            {
                _visible = k;
                used = total;
                break;
            }
        }

        if (_visible == chips)
        {
            _more.Text = "";
            _more.Measure(new Size(0, 0));
        }
        else
        {
            height = Math.Max(height, _more.DesiredSize.Height);
        }

        return new Size(Math.Min(used, double.IsInfinity(available.Width) ? used : available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var x = 0.0;
        var chips = Children.Count - 1;
        for (var i = 0; i < chips; i++)
        {
            var child = Children[i];
            if (i < _visible)
            {
                child.Arrange(new Rect(x, (final.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + Gap;
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        if (_visible < chips)
        {
            _more.Arrange(new Rect(x, (final.Height - _more.DesiredSize.Height) / 2, _more.DesiredSize.Width, _more.DesiredSize.Height));
        }
        else
        {
            _more.Arrange(new Rect(0, 0, 0, 0));
        }

        return final;
    }
}
