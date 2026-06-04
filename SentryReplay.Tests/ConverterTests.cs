using System.Windows;

namespace SentryReplay.Tests;

public sealed class ConverterTests
{
    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void BoolToVisibilityConverter_MapsBooleanToVisibility(bool input, Visibility expected)
    {
        var converter = new BoolToVisibilityConverter();

        var result = converter.Convert(input, typeof(Visibility), null, null);

        result.ShouldBe(expected);
    }

    [Fact]
    public void BoolToVisibilityConverter_CollapsesNonBooleanValues()
    {
        var converter = new BoolToVisibilityConverter();

        converter.Convert(null, typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
        converter.Convert("true", typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
    }
}
