using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BACprobe.App;

/// <summary>true ⇄ false. Used to stop a drop-down's toggle button from reopening the drop-down it just closed.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>A count to Visible/Collapsed: visible when non-zero, or when zero with ConverterParameter="Zero" (for empty-list hints).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var zero = value is int n && n == 0;
        var wantZero = string.Equals(parameter as string, "Zero", StringComparison.OrdinalIgnoreCase);
        return zero == wantZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Text to Visible/Collapsed: collapsed when null or empty.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
