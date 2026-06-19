using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DerivedOutputDownloader3DX.UI.Converters;

/// <summary>Convertit une chaîne : non-vide → Visible, vide/null → Collapsed.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
