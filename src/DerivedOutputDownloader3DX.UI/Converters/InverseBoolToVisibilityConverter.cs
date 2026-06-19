using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DerivedOutputDownloader3DX.UI.Converters;

/// <summary>Convertit un bool : true → Collapsed, false → Visible (inverse de BoolToVisibility).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
