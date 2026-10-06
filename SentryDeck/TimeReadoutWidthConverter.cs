using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace SentryDeck;

/// <summary>
/// The width the time readout beside the seek bar keeps while a clip is open: the width of the longest "position / duration" that clip can show, in the readout's own font.
/// Multi-binding: [0] the clip's duration as the readout shows it, [1] the readout TextBlock, whose first run shows the position and last run the duration.
/// </summary>
/// <remarks>
/// A readout as wide as its text shifted the seek bar and its markers each time the position gained a digit, such as when playback crossed 10:00.
/// A fixed width that fits a 10-minute clip took about 15 px from the seek bar on every shorter clip, and the bar is already narrow in a small window.
/// The position never has more digits than the duration, so the duration alone sets the widest text for the whole clip.
/// </remarks>
public sealed class TimeReadoutWidthConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string duration || values[1] is not TextBlock readout)
        {
            return 0d;
        }

        var widest = new FormattedText(
            WidestText(duration, Separator(readout)),
            culture ?? CultureInfo.CurrentCulture,
            readout.FlowDirection,
            new Typeface(readout.FontFamily, readout.FontStyle, readout.FontWeight, readout.FontStretch),
            readout.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(readout).PixelsPerDip);

        // Rounded up, so layout rounding can't leave the readout a fraction of a pixel narrower than its longest text.
        return Math.Ceiling(widest.WidthIncludingTrailingWhitespace);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// The longest text the readout shows for a clip of this duration, e.g. "0:00 / 0:00" for a 4:02 clip and "00:00 / 00:00" for a 10:33 one.
    /// </summary>
    /// <remarks>
    /// Its digits are all zeros so that clips of one length class get one width, which holds as long as the font's digits share a width; the readout uses Consolas, where every character does.
    /// </remarks>
    internal static string WidestText(string duration, string separator)
    {
        var shape = string.Concat(duration.Select(character => char.IsAsciiDigit(character) ? '0' : character));
        return $"{shape}{separator}{shape}";
    }

    // Read from the readout rather than assumed, because XAML adds a space between runs written on separate lines, and a width one character short brings the shifting back.
    private static string Separator(TextBlock readout) =>
        string.Concat(readout.Inlines.OfType<Run>().Skip(1).SkipLast(1).Select(run => run.Text));
}
