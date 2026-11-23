using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LanTransfer.Protocol;

namespace LanTransfer.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (Invert)
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DeviceTypeToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is DeviceType dt
            ? dt switch
            {
                DeviceType.Windows => "IconWindows",
                DeviceType.Ios => "IconIos",
                _ => "IconAndroid",
            }
            : "IconAndroid";
        return Application.Current.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DeviceTypeToAccentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AndroidBrush = new((Color)ColorConverter.ConvertFromString("#10B981"));
    private static readonly SolidColorBrush WindowsBrush = new((Color)ColorConverter.ConvertFromString("#2563EB"));
    private static readonly SolidColorBrush IosBrush = new((Color)ColorConverter.ConvertFromString("#6366F1"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is DeviceType dt
        ? dt switch
        {
            DeviceType.Windows => WindowsBrush,
            DeviceType.Ios => IosBrush,
            _ => AndroidBrush,
        }
        : AndroidBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
