using System.Globalization;
using System.Windows;
using FoundrySummarizer.Wpf.Converters;

namespace FoundrySummarizer.Wpf.Tests;

public class ConverterTests
{
    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    [InlineData(null, Visibility.Collapsed)]          // an unset binding hides the element
    [InlineData("true", Visibility.Collapsed)]        // only a real bool counts
    public void BooleanToVisibility_ShowsOnlyForTrue(object? value, Visibility expected)
    {
        Assert.Equal(expected, new BooleanToVisibilityConverter().Convert(value!, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    [InlineData(null, Visibility.Visible)]
    public void InverseBooleanToVisibility_HidesOnlyForTrue(object? value, Visibility expected)
    {
        Assert.Equal(expected, new InverseBooleanToVisibilityConverter().Convert(value!, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(Visibility.Visible, true)]
    [InlineData(Visibility.Collapsed, false)]
    [InlineData(Visibility.Hidden, false)]
    public void Converters_ConvertBack(Visibility visibility, bool visible)
    {
        Assert.Equal(visible, new BooleanToVisibilityConverter().ConvertBack(visibility, typeof(bool), null!, CultureInfo.InvariantCulture));
        Assert.Equal(!visible, new InverseBooleanToVisibilityConverter().ConvertBack(visibility, typeof(bool), null!, CultureInfo.InvariantCulture));
    }
}
