using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace SentryDeck;

/// <summary>
/// Converts true to Visible and all other values to Collapsed.
/// </summary>
public sealed class BoolToVisibilityConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Picks one of two texts by a boolean: ConverterParameter is "text when true|text when false".
/// Lets a control that changes what it does, such as Play/Pause, say what it will do now instead of naming both.
/// </summary>
public sealed class BoolToTextConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var texts = (parameter as string)?.Split('|');
        if (texts is not { Length: 2 })
        {
            return string.Empty;
        }

        return value is true ? texts[0] : texts[1];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The name screen readers announce for a clip in the list, e.g. "Saturday, December 16, 2023, 3:53 PM, Honk, Hutto, about 5 min".
/// Multi-binding: [0] the clip's Timestamp, [1] its reason label, [2] its city, [3] its length as the list shows it.
/// </summary>
/// <remarks>
/// Without a name, a row was read out as its raw timestamp ("12/16/2023 15:53:27") and nothing else.
/// The reason and length arrive from the clip card's own converters, and the day and time from the same helpers as the list's day headers and rows, so what is read matches what is shown.
/// </remarks>
public sealed class ClipAutomationNameConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = new List<string>();

        if (values.Length > 0 && values[0] is DateTime timestamp)
        {
            parts.Add(ClipDisplay.DayHeader(timestamp));
            parts.Add(ClipDisplay.RowTime(timestamp));
        }

        // Reason and city; a clip without event data has neither, and an empty part would be read as a stray pause.
        foreach (var value in values.Skip(1).Take(2))
        {
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        // The list marks its estimate with a tilde, a clip shorter than a minute with a less-than sign, and a clip with no footage with a dash.
        // Read aloud, none of them means what it shows, and a screen reader that skips the less-than sign would give a clip of a few seconds a whole minute.
        if (values.Length > 3 && values[3] is string length)
        {
            if (length.StartsWith('~'))
            {
                parts.Add($"about {length[1..]}");
            }
            else if (length.StartsWith('<'))
            {
                parts.Add($"under {length[1..]}");
            }
            else if (length is not ("" or "—"))
            {
                parts.Add(length);
            }
        }

        return string.Join(", ", parts);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
