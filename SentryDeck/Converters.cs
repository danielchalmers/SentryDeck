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
