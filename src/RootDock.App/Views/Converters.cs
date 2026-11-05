using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RootDock.App.Views;

/// <summary>Visível quando o valor (enum/texto) é igual a um dos itens do parâmetro "A|B".</summary>
public sealed class EqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var options = (parameter as string ?? "").Split('|');
        return options.Contains(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visível quando o texto não é vazio.</summary>
public sealed class NotEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Largura menos uma reserva (ex.: nome da sessão deixa espaço para o "+N" das tags).</summary>
public sealed class SubtractConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is double width && double.TryParse(parameter as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minus)
            ? Math.Max(0, width - minus)
            : double.PositiveInfinity;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true = escondido, false = visível.</summary>
public sealed class InverseBoolVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
