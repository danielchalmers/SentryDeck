namespace SentryReplay.Tests;

public sealed class MainWindowTests
{
    [Theory]
    [InlineData(0, 0, 0, "0:00")]
    [InlineData(0, 1, 30, "1:30")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 0, 0, "1:00:00")]
    [InlineData(2, 30, 45, "2:30:45")]
    public void FormatTimeSpan_UsesPlayerTimeFormat(int hours, int minutes, int seconds, string expected)
    {
        MainWindow.FormatTimeSpan(new TimeSpan(hours, minutes, seconds)).ShouldBe(expected);
    }
}
