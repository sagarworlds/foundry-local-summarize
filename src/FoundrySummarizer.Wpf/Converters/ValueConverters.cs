using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FoundrySummarizer.Wpf.Converters;

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}

public class ScoreToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#34d399"));
    private static readonly SolidColorBrush YellowBrush = new((Color)ColorConverter.ConvertFromString("#fbbf24"));
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#f87171"));
    private static readonly SolidColorBrush MutedBrush = new((Color)ColorConverter.ConvertFromString("#71717a"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            if (d >= 0.75) return GreenBrush;
            if (d >= 0.50) return YellowBrush;
            return RedBrush;
        }
        return MutedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#34d399"));
    private static readonly SolidColorBrush BlueBrush = new((Color)ColorConverter.ConvertFromString("#38bdf8"));
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#f87171"));
    private static readonly SolidColorBrush DefaultBrush = new((Color)ColorConverter.ConvertFromString("#a1a1aa"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToUpperInvariant() ?? "";
        if (status.Contains("EXCELLENT") || status.Contains("PASS") || status.Contains("ACTIVE") || status.Contains("GOOD"))
            return GreenBrush;
        if (status.Contains("PRIVACY") || status.Contains("LOCAL") || status.Contains("FOUNDRY"))
            return BlueBrush;
        if (status.Contains("BLOCKED") || status.Contains("FAIL") || status.Contains("VIOLATION") || status.Contains("RED"))
            return RedBrush;

        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
