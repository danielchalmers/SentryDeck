using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace SentryReplay;

/// <summary>
/// Converts a TimeSpan to a formatted string (mm:ss or hh:mm:ss).
/// </summary>
public sealed class TimeSpanToStringConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }

        return "0:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
