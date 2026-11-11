using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BlueHarbor.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace BlueHarbor.Helpers;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Mint = Frozen("#4DE3C1");
    private static readonly Brush Amber = Frozen("#F5B84C");
    private static readonly Brush Red = Frozen("#FF6B6B");
    private static readonly Brush Muted = Frozen("#7F94B0");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ReceiverLinkState state)
        {
            return Muted;
        }

        return state switch
        {
            ReceiverLinkState.Connected => Mint,
            ReceiverLinkState.Error => Red,
            ReceiverLinkState.Off => Muted,
            _ => Amber
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class StatusGlowConverter : IValueConverter
{
    private static readonly Brush Mint = Frozen("#244DE3C1");
    private static readonly Brush Amber = Frozen("#22F5B84C");
    private static readonly Brush Red = Frozen("#22FF6B6B");
    private static readonly Brush Muted = Frozen("#167F94B0");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ReceiverLinkState state)
        {
            return Muted;
        }

        return state switch
        {
            ReceiverLinkState.Connected => Mint,
            ReceiverLinkState.Error => Red,
            ReceiverLinkState.Off => Muted,
            _ => Amber
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
