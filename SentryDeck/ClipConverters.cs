using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SentryDeck;

/// <summary>
/// Friendly event-reason label for a <see cref="CamEvent"/> (e.g. "Sentry", "Honk", "Saved").
/// </summary>
public sealed class ReasonLabelConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ClipDisplay.ReasonLabel(value as CamEvent);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The stable reason category key for a <see cref="CamEvent"/> (see <see cref="ClipDisplay.ReasonKey"/>).
/// Reason-colored overlays bind this into DataTriggers that pick a theme brush via
/// <c>DynamicResource</c>, so the color follows a live OS light/dark switch. (Resolving the brush in
/// the converter instead returned a one-time snapshot that stayed on the old theme.)
/// </summary>
public sealed class ReasonKeyConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ClipDisplay.ReasonKey(value as CamEvent);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A friendly, culture-aware clip date/time. ConverterParameter selects the part: "date" →
/// "Mon, Dec 16", "time" → "3:53 PM". The clip card lays those on opposite ends of the row, so
/// no middot separator is needed.
/// </summary>
public sealed class FriendlyDateConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt)
            return string.Empty;

        var c = CultureInfo.CurrentCulture;
        return (parameter as string)?.ToLowerInvariant() switch
        {
            "date" => dt.ToString("ddd, MMM d", c),
            "time" => dt.ToString("t", c),
            _ => $"{dt.ToString("ddd, MMM d", c)} {dt.ToString("t", c)}",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A sticky day-group header: "Today", "Yesterday", or a full date.
/// </summary>
public sealed class DayGroupHeaderConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt)
            return string.Empty;

        var date = dt.Date;
        var today = DateTime.Today;
        if (date == today)
            return "Today";
        if (date == today.AddDays(-1))
            return "Yesterday";

        return date.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Groups clips by calendar day (used by the list's <c>PropertyGroupDescription</c>).
/// </summary>
public sealed class DateOnlyConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? dt.Date : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Estimated clip duration as a human string, e.g. "~5 min" (uses the modeled
/// <see cref="ClipTimeline.Duration"/> = chunk count × 60s).
/// </summary>
public sealed class ClipDurationConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CamClip clip)
            return string.Empty;

        var duration = new ClipTimeline(clip.Chunks).Duration;
        var minutes = (int)Math.Round(duration.TotalMinutes);
        if (minutes <= 0)
            return "—";

        return minutes < 60
            ? $"~{minutes} min"
            : $"~{minutes / 60}h {minutes % 60}m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Loads a clip thumbnail if the file exists; returns null otherwise so a fallback can show.
/// Pass ConverterParameter="fallback" to instead get a Visibility that is Visible when missing.
/// </summary>
public sealed class ThumbnailConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        var exists = !string.IsNullOrEmpty(path) && File.Exists(path);

        if (string.Equals(parameter as string, "fallback", StringComparison.OrdinalIgnoreCase))
            return exists ? Visibility.Collapsed : Visibility.Visible;

        if (!exists)
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = 192;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visibility.Visible when the event has usable coordinates for a map lookup.
/// </summary>
public sealed class MapAvailabilityConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ClipDisplay.HasLocation(value as CamEvent) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Shared geometry for the seek-bar overlays. WPF's <c>Track</c> insets the thumb's travel by the
/// thumb's own width, so the thumb CENTER spans <c>[ThumbWidth/2, width − ThumbWidth/2]</c> — not
/// <c>[0, width]</c>. Every overlay (played fill, event marker, chunk/gap ticks, selection band)
/// maps its fraction into that same span so it lines up with the thumb at the extremes instead of
/// diverging by half a thumb.
/// </summary>
public static class SeekBarMetrics
{
    /// <summary>Must match the Thumb template's width in the <c>SeekSlider</c> style.</summary>
    public const double ThumbWidth = 20.0;

    /// <summary>The pixel position of the thumb center for a 0..1 fraction on a rail of the given width.</summary>
    public static double ThumbCenterFor(double fraction, double railWidth) =>
        (ThumbWidth / 2) + (Math.Clamp(fraction, 0, 1) * TravelWidth(railWidth));

    /// <summary>The width the thumb center actually travels: the rail minus one thumb width.</summary>
    public static double TravelWidth(double railWidth) => Math.Max(0, railWidth - ThumbWidth);
}

/// <summary>
/// Played-track width for the seek bar: from the rail's left edge to the thumb center.
/// [0] slider value (0..1), [1] rail ActualWidth.
/// </summary>
public sealed class SeekFillWidthConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double value && values[1] is double width && width > 0)
        {
            return SeekBarMetrics.ThumbCenterFor(value, width);
        }

        return 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Left offset for a seek-bar overlay (event marker or chunk tick): a left <see cref="Thickness"/>
/// at the thumb-center position for the fraction. [0] fraction (0..1), [1] rail ActualWidth.
/// Mirrors <see cref="SeekFillWidthConverter"/> so overlays track the played fill and the thumb,
/// and reflow on resize.
/// </summary>
public sealed class SeekOffsetConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double fraction && values[1] is double width && width > 0)
        {
            return new Thickness(SeekBarMetrics.ThumbCenterFor(fraction, width), 0, 0, 0);
        }

        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Width of the selection-range highlight on the seek bar: (end − start) × the thumb's travel
/// width. [0] start fraction (0..1), [1] end fraction (0..1), [2] rail ActualWidth. Pairs with
/// <see cref="SeekOffsetConverter"/> (which places the highlight's left edge at the start
/// fraction's thumb center) so the band ends exactly at the end fraction's thumb center.
/// </summary>
public sealed class SelectionWidthConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 3
            && values[0] is double start
            && values[1] is double end
            && values[2] is double width
            && width > 0)
        {
            return Math.Clamp(end - start, 0, 1) * SeekBarMetrics.TravelWidth(width);
        }

        return 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visibility.Visible when the bound clip is the one currently playing.
/// Multi-binding: [0] the clip, [1] the view-model's NowPlayingClip.
/// </summary>
public sealed class NowPlayingConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]))
            return Visibility.Visible;

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
