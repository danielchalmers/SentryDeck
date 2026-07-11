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

    // The seek bar overlays position against the THUMB CENTER, which WPF's Track insets by half
    // the thumb's width at each end: fraction f on a rail of width W maps to 10 + f × (W − 20).

    [Fact]
    public void SeekOffsetConverter_MapsFractionToThumbCenter()
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([0.5, 200d], typeof(Thickness), null, null);

        result.Left.ShouldBe(100); // 10 + 0.5 × 180
        result.Top.ShouldBe(0);
    }

    [Theory]
    [InlineData(0.0, 200, 10)]   // thumb center at the left extreme sits half a thumb in
    [InlineData(1.0, 200, 190)]  // ... and half a thumb short of the right edge
    [InlineData(1.5, 200, 190)]  // clamps above 1
    [InlineData(-0.5, 200, 10)]  // clamps below 0
    public void SeekOffsetConverter_ClampsFractionToThumbTravel(double fraction, double width, double expectedLeft)
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

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(1.0)]
    public void SeekFillWidth_EndsExactlyAtTheThumbCenter(double fraction)
    {
        // The played fill must meet the thumb at every position, including the extremes (it used
        // to diverge by half a thumb at 0 and 1).
        var fill = (double)new SeekFillWidthConverter().Convert([fraction, 200d], typeof(double), null, null);
        var offset = (Thickness)new SeekOffsetConverter().Convert([fraction, 200d], typeof(Thickness), null, null);

        fill.ShouldBe(offset.Left);
    }

    [Fact]
    public void SelectionWidth_SpansExactlyBetweenTheMarkOffsets()
    {
        // Band left edge (start offset) + band width must land on the end mark's offset.
        var start = (Thickness)new SeekOffsetConverter().Convert([0.25, 200d], typeof(Thickness), null, null);
        var end = (Thickness)new SeekOffsetConverter().Convert([0.75, 200d], typeof(Thickness), null, null);
        var width = (double)new SelectionWidthConverter().Convert([0.25, 0.75, 200d], typeof(double), null, null);

        (start.Left + width).ShouldBe(end.Left);
    }
}
