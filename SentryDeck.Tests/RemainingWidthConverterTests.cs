using System.Windows;

namespace SentryDeck.Tests;

/// <summary>
/// The sidebar's widest setting for the window's current width, which used to stay 440 however little room that left the player.
/// </summary>
public sealed class RemainingWidthConverterTests
{
    private static readonly RemainingWidthConverter Converter = new() { Maximum = 440, Reserved = 864 };

    [Fact]
    public void Convert_RoomToSpare_CapsAtTheMaximum()
    {
        Converter.Convert(1600d, typeof(double), null, null).ShouldBe(440d);
    }

    [Fact]
    public void Convert_NarrowGrid_LeavesTheOtherColumnsTheirReservedWidth()
    {
        Converter.Convert(1184d, typeof(double), null, null).ShouldBe(320d);
    }

    [Fact]
    public void Convert_GridNarrowerThanTheReservedWidth_NeverGoesNegative()
    {
        // A column rejects a negative MaxWidth, so this would throw out of the layout pass.
        Converter.Convert(500d, typeof(double), null, null).ShouldBe(0d);
    }

    [Fact]
    public void Convert_BeforeTheFirstLayout_KeepsTheMaximum()
    {
        Converter.Convert(0d, typeof(double), null, null).ShouldBe(440d);
        Converter.Convert(DependencyProperty.UnsetValue, typeof(double), null, null).ShouldBe(440d);
    }
}
