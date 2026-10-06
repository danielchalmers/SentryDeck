using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SentryDeck.Tests;

/// <summary>
/// The width the time readout beside the seek bar keeps for the open clip, measured on a TextBlock written like the one in the window.
/// </summary>
public sealed class TimeReadoutWidthConverterTests
{
    private static readonly TimeReadoutWidthConverter Converter = new();

    [Theory]
    [InlineData("4:02", "0:00 / 0:00")]
    [InlineData("10:33", "00:00 / 00:00")]
    [InlineData("1:02:03", "0:00:00 / 0:00:00")]
    public void WidestText_ClipDuration_HasTheDurationsShapeOnBothSides(string duration, string expected)
    {
        TimeReadoutWidthConverter.WidestText(duration, " / ").ShouldBe(expected);
    }

    [Theory]
    [InlineData("0:00", "4:02")]
    [InlineData("0:00", "10:33")]
    [InlineData("9:59", "10:33")]
    [InlineData("59:59", "1:02:03")]
    public void Convert_PositionGainsADigitDuringPlayback_KeepsTheReadoutWidth(string earlyPosition, string duration)
    {
        // The seek bar takes whatever width the readout leaves, so a readout that grew with the position shifted the bar and its markers mid-playback.
        ReadoutWidth(earlyPosition, duration).ShouldBe(ReadoutWidth(duration, duration));
    }

    [Fact]
    public void Convert_ClipUnderTenMinutes_TakesNoMoreThanItsLongestText()
    {
        // A readout always as wide as a 10-minute clip's took about 15 px from an already narrow seek bar on every shorter clip.
        ReadoutWidth("4:02", "4:02").ShouldBe(Math.Ceiling(ReadoutWidth("4:02", "4:02", sized: false)));
        ReadoutWidth("0:00", "4:02").ShouldBeLessThan(ReadoutWidth("0:00", "10:33"));
        ReadoutWidth("0:00", "10:33").ShouldBeLessThan(ReadoutWidth("0:00", "1:02:03"));
    }

    [Fact]
    public void Convert_ClipsUnderTenMinutes_ShareOneWidth()
    {
        // With nothing open the readout shows 0:00, so opening a short clip doesn't move the seek bar either.
        ReadoutWidth("0:00", "4:02").ShouldBe(ReadoutWidth("0:00", "0:00"));
        ReadoutWidth("0:00", "4:02").ShouldBe(ReadoutWidth("0:00", "9:59"));
    }

    [Fact]
    public void Convert_WithoutTheReadout_SetsNoMinimum()
    {
        Converter.Convert(["4:02", DependencyProperty.UnsetValue], typeof(double), null, null).ShouldBe(0d);
        Converter.Convert([DependencyProperty.UnsetValue, DependencyProperty.UnsetValue], typeof(double), null, null).ShouldBe(0d);
    }

    // Written like the window's readout, with each run on its own line, which makes XAML put a space between the runs.
    private static double ReadoutWidth(string position, string duration, bool sized = true) => OnStaThread(() =>
    {
        var readout = (TextBlock)XamlReader.Parse(
            $"""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       FontFamily="Consolas"
                       FontSize="13"
                       TextAlignment="Right">
                <Run Text="{position}" />
                <Run Text=" / " />
                <Run Text="{duration}" />
            </TextBlock>
            """);

        if (sized)
        {
            readout.MinWidth = (double)Converter.Convert([duration, readout], typeof(double), null, null);
        }

        readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return readout.DesiredSize.Width;
    });

    // WPF controls need an STA thread, which xUnit's test threads are not.
    private static T OnStaThread<T>(Func<T> function)
    {
        T result = default;
        ExceptionDispatchInfo failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
        return result;
    }
}
