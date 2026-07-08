using System.Windows;

namespace SentryDeck.Tests;

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

    [Fact]
    public void SeekOffsetConverter_ReturnsLeftOffsetOfFractionTimesWidth()
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([0.5, 200d], typeof(Thickness), null, null);

        result.Left.ShouldBe(100);
        result.Top.ShouldBe(0);
    }

    [Theory]
    [InlineData(1.5, 200, 200)] // clamps above 1
    [InlineData(-0.5, 200, 0)]  // clamps below 0
    public void SeekOffsetConverter_ClampsFraction(double fraction, double width, double expectedLeft)
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([fraction, width], typeof(Thickness), null, null);

        result.Left.ShouldBe(expectedLeft);
    }

    [Fact]
    public void SeekOffsetConverter_ZeroWidthYieldsZeroOffset()
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([0.5, 0d], typeof(Thickness), null, null);

        result.ShouldBe(new Thickness(0));
    }
}
