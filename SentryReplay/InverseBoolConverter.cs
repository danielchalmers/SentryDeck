using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace SentryReplay;

/// <summary>
/// Inverts a boolean value.
/// </summary>
public sealed class InverseBoolConverter : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }

        return false;
    }
}
