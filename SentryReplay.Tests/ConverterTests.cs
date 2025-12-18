using Shouldly;

namespace SentryReplay.Tests;

/// <summary>
/// Tests for value converters used in the UI.
/// </summary>
public class ConverterTests
{
    #region TimeSpanToStringConverter Tests

    [Theory]
    [InlineData(0, 0, 0, "0:00")]
    [InlineData(0, 1, 30, "1:30")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 0, 0, "1:00:00")]
    [InlineData(2, 30, 45, "2:30:45")]
    public void TimeSpanToStringConverter_FormatsCorrectly(int hours, int minutes, int seconds, string expected)
    {
        // Arrange
        var converter = new TimeSpanToStringConverter();
        var timeSpan = new TimeSpan(hours, minutes, seconds);

        // Act
        var result = converter.Convert(timeSpan, typeof(string), null, null);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void TimeSpanToStringConverter_HandlesNull()
    {
        // Arrange
        var converter = new TimeSpanToStringConverter();

        // Act
        var result = converter.Convert(null, typeof(string), null, null);

        // Assert
        result.ShouldBe("0:00");
    }

    [Fact]
    public void TimeSpanToStringConverter_HandlesInvalidType()
    {
        // Arrange
        var converter = new TimeSpanToStringConverter();

        // Act
        var result = converter.Convert("not a timespan", typeof(string), null, null);

        // Assert
        result.ShouldBe("0:00");
    }

    #endregion

    #region BoolToVisibilityConverter Tests

    [Fact]
    public void BoolToVisibilityConverter_True_ReturnsVisible()
    {
        // Arrange
        var converter = new BoolToVisibilityConverter();

        // Act
        var result = converter.Convert(true, typeof(System.Windows.Visibility), null, null);

        // Assert
        result.ShouldBe(System.Windows.Visibility.Visible);
    }

    [Fact]
    public void BoolToVisibilityConverter_False_ReturnsCollapsed()
    {
        // Arrange
        var converter = new BoolToVisibilityConverter();

        // Act
        var result = converter.Convert(false, typeof(System.Windows.Visibility), null, null);

        // Assert
        result.ShouldBe(System.Windows.Visibility.Collapsed);
    }

    [Fact]
    public void BoolToVisibilityConverter_Null_ReturnsCollapsed()
    {
        // Arrange
        var converter = new BoolToVisibilityConverter();

        // Act
        var result = converter.Convert(null, typeof(System.Windows.Visibility), null, null);

        // Assert
        result.ShouldBe(System.Windows.Visibility.Collapsed);
    }

    #endregion

    #region InverseBoolConverter Tests

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBoolConverter_InvertsValue(bool input, bool expected)
    {
        // Arrange
        var converter = new InverseBoolConverter();

        // Act
        var result = converter.Convert(input, typeof(bool), null, null);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void InverseBoolConverter_Null_ReturnsFalse()
    {
        // Arrange
        var converter = new InverseBoolConverter();

        // Act
        var result = converter.Convert(null, typeof(bool), null, null);

        // Assert
        result.ShouldBe(false);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBoolConverter_ConvertBack_InvertsValue(bool input, bool expected)
    {
        // Arrange
        var converter = new InverseBoolConverter();

        // Act
        var result = converter.ConvertBack(input, typeof(bool), null, null);

        // Assert
        result.ShouldBe(expected);
    }

    #endregion
}
