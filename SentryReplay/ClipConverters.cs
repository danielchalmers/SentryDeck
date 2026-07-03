using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SentryReplay;

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
/// Maps a <see cref="CamEvent"/> reason to its accent brush from the current theme.
/// </summary>
public sealed class ReasonBrushConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = ClipDisplay.ReasonKey(value as CamEvent) switch
        {
            ClipDisplay.ReasonSentry => "SystemFillColorAttentionBrush",
            ClipDisplay.ReasonHonk => "SystemFillColorCautionBrush",
            ClipDisplay.ReasonAlert => "SystemFillColorCriticalBrush",
            ClipDisplay.ReasonSaved => "AccentFillColorDefaultBrush",
            _ => "TextFillColorTertiaryBrush",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

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
/// Played-track width for the seek bar: [0] slider value (0..1), [1] rail ActualWidth.
/// </summary>
public sealed class SeekFillWidthConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double value && values[1] is double width && width > 0)
        {
            return Math.Clamp(value, 0, 1) * width;
        }

        return 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Left offset for a seek-bar overlay (event marker or chunk tick): a left <see cref="Thickness"/>
/// of position × rail width. [0] fraction (0..1), [1] rail ActualWidth. Mirrors
/// <see cref="SeekFillWidthConverter"/> so overlays track the played fill and reflow on resize.
/// </summary>
public sealed class SeekOffsetConverter : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double fraction && values[1] is double width && width > 0)
        {
            return new Thickness(Math.Clamp(fraction, 0, 1) * width, 0, 0, 0);
        }

        return new Thickness(0);
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
