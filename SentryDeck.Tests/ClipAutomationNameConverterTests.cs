using System.Globalization;
using System.Windows;

namespace SentryDeck.Tests;

/// <summary>
/// What screen readers announce for a clip in the list, which was only its raw timestamp before.
/// </summary>
public sealed class ClipAutomationNameConverterTests
{
    // A Saturday long past, so the day can't read as "Today" or "Yesterday".
    private static readonly DateTime Moment = new(2023, 12, 16, 15, 53, 27);

    private static readonly CultureInfo English = new("en-US");

    // The culture decides the time's spacing (newer ICU puts a narrow no-break space before PM), so the expectation formats it the same way.
    private static readonly string Time = Moment.ToString("t", English);

    [Fact]
    public void Convert_EveryPart_ReadsWhenWhyWhereAndHowLong()
    {
        var name = ConvertInEnglish(Moment, "Honk", "Hutto", "~5 min");

        name.ShouldBe($"Saturday, December 16, 2023, {Time}, Honk, Hutto, about 5 min");
    }

    [Fact]
    public void Convert_NoCityAndNoFootage_LeavesThemOutWithoutStrayPauses()
    {
        var name = ConvertInEnglish(Moment, "Recent", null, "—");

        name.ShouldBe($"Saturday, December 16, 2023, {Time}, Recent");
    }

    [Fact]
    public void Convert_LengthOverAnHour_ReadsAsAnEstimate()
    {
        var name = ConvertInEnglish(Moment, "Sentry", " ", "~1h 35m");

        name.ShouldBe($"Saturday, December 16, 2023, {Time}, Sentry, about 1h 35m");
    }

    [Fact]
    public void Convert_ClipShorterThanAMinute_ReadsAsUnderAMinute()
    {
        var name = ConvertInEnglish(Moment, "Saved", null, "<1 min");

        name.ShouldBe($"Saturday, December 16, 2023, {Time}, Saved, under 1 min");
    }

    [Fact]
    public void Convert_UnresolvedBindings_StillNamesTheDayAndTime()
    {
        // While a row is being realized its bindings can still be unset, and the name must neither throw nor read the placeholder.
        var unset = DependencyProperty.UnsetValue;

        var name = ConvertInEnglish(Moment, unset, unset, unset);

        name.ShouldBe($"Saturday, December 16, 2023, {Time}");
    }

    // The list formats dates in the user's culture, so the test pins one.
    private static string ConvertInEnglish(params object[] values)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = English;
        try
        {
            return (string)new ClipAutomationNameConverter().Convert(values, typeof(string), null, English);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
