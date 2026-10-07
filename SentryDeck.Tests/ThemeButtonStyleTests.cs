using System.Windows;
using System.Windows.Baml2006;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xaml;

namespace SentryDeck.Tests;

/// <summary>
/// The app's flat button styles, exercised on real buttons styled by the shipped Theme.xaml over WPF's Fluent theme.
/// </summary>
public sealed class ThemeButtonStyleTests
{
    [Theory]
    [InlineData("SubtleIconButton", "Dark")]
    [InlineData("SubtleIconButton", "Light")]
    [InlineData("PressScaleButton", "Dark")]
    [InlineData("PressScaleButton", "Light")]
    public void ButtonStyle_DisabledWithTextBlockContent_DimsTheGlyphAndLabel(string styleKey, string fluentTheme)
    {
        StaThread.Run(() =>
        {
            var (host, button, glyph, label) = CreateIconLabelButton(styleKey, fluentTheme);

            button.IsEnabled = false;
            StaThread.DrainDispatcher(host);

            // The app's buttons hold explicit TextBlocks, which take their color from the button rather than from the theme's dimmed content presenter.
            var disabledColor = ColorOf(host.FindResource("TextFillColorDisabledBrush"));
            ColorOf(glyph.Foreground).ShouldBe(disabledColor);
            ColorOf(label.Foreground).ShouldBe(disabledColor);
        });
    }

    [Theory]
    [InlineData("SubtleIconButton")]
    [InlineData("PressScaleButton")]
    public void ButtonStyle_Enabled_KeepsTheLabelReadable(string styleKey)
    {
        StaThread.Run(() =>
        {
            var (host, _, _, label) = CreateIconLabelButton(styleKey, "Dark");

            ColorOf(label.Foreground).ShouldNotBe(ColorOf(host.FindResource("TextFillColorDisabledBrush")));
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void SubtleIconButton_Disabled_StaysFlat(string fluentTheme)
    {
        StaThread.Run(() =>
        {
            var (host, button, _, _) = CreateIconLabelButton("SubtleIconButton", fluentTheme);

            button.IsEnabled = false;
            StaThread.DrainDispatcher(host);

            // The theme's disabled fill and outline would turn a flat button into a pill that looks more pressable than the enabled one.
            var border = button.Template.FindName("ContentBorder", button).ShouldBeOfType<Border>();
            ColorOf(border.Background).A.ShouldBe((byte)0);
            ColorOf(border.BorderBrush).A.ShouldBe((byte)0);
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void SubtleIconButton_CheckedToggleButton_KeepsTheFlatLook(string fluentTheme)
    {
        StaThread.Run(() =>
        {
            // The Trim button is a toggle so screen readers can tell whether the trim panel is open; on screen the open panel already says so.
            var (host, toggle, _, label) = CreateIconLabelButton("SubtleIconButton", fluentTheme, new ToggleButton { IsChecked = true });

            var border = toggle.Template.FindName("ContentBorder", toggle).ShouldBeOfType<Border>();
            ColorOf(border.Background).A.ShouldBe((byte)0);
            ColorOf(label.Foreground).ShouldNotBe(ColorOf(host.FindResource("TextFillColorDisabledBrush")));
        });
    }

    private static (Border Host, ButtonBase Button, TextBlock Glyph, TextBlock Label) CreateIconLabelButton(string styleKey, string fluentTheme, ButtonBase button = null)
    {
        // Touching Application registers the pack:// scheme that theme dictionaries load through, which a test process otherwise lacks.
        _ = Application.Current;

        // The app's styles build on the Fluent ones through StaticResource, which the app resolves from its application resources, and a test has no application.
        // Loading the compiled Theme.xaml into a dictionary that already holds the Fluent theme lets those lookups resolve while it loads instead.
        var theme = new ResourceDictionary();
        theme.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{fluentTheme}.xaml"),
        });
        var compiledTheme = Application.GetResourceStream(new Uri("/SentryDeck;component/Resources/Theme.xaml", UriKind.Relative));
        using (var reader = new Baml2006Reader(compiledTheme.Stream, new XamlReaderSettings { LocalAssembly = typeof(App).Assembly }))
        {
            XamlServices.Transform(reader, new XamlObjectWriter(reader.SchemaContext, new XamlObjectWriterSettings { RootObjectInstance = theme }));
        }

        // The dictionaries sit above the button, as they do in the app, so the style's own resources take precedence over the theme's.
        var host = new Border();
        host.Resources.MergedDictionaries.Add(theme);

        var glyph = new TextBlock { Text = "\uE7C1", Style = (Style)host.FindResource("AppGlyphInButton") };
        var label = new TextBlock { Text = "Event" };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(label);

        button ??= new Button();
        button.Content = content;
        button.Style = (Style)host.FindResource(styleKey);
        host.Child = button;

        host.Measure(new Size(200, 100));
        host.Arrange(new Rect(0, 0, 200, 100));
        StaThread.DrainDispatcher(host);

        return (host, button, glyph, label);
    }

    private static Color ColorOf(object brush) => brush.ShouldBeOfType<SolidColorBrush>().Color;
}
