using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;

namespace SentryDeck.Tests;

/// <summary>
/// What screen readers find inside the seek bar's ticks and the camera strip, both of which a plain ItemsControl fills with wrappers named by type name or raw value.
/// </summary>
public sealed class ItemsControlAutomationTests
{
    [Fact]
    public void DecorativeItemsControl_SeekTicks_LeaveNothingForScreenReaders()
    {
        StaThread.Run(() =>
        {
            var ticks = new DecorativeItemsControl
            {
                ItemsSource = new[] { 0.0936324907, 0.5 },
                ItemTemplate = Template("<Rectangle Width='1' Height='8' />"),
            };
            Realize(ticks);

            var peer = UIElementAutomationPeer.CreatePeerForElement(ticks);

            // A plain ItemsControl lists one item per tick here, named "0.0936324907" and "0.5".
            (peer.GetChildren() ?? []).ShouldBeEmpty();
            peer.IsContentElement().ShouldBeFalse();
        });
    }

    [Fact]
    public void ControlGroupItemsControl_CameraStrip_ExposesTheTilesAsOneNamedGroup()
    {
        StaThread.Run(() =>
        {
            var strip = new ControlGroupItemsControl
            {
                ItemsSource = new[]
                {
                    new CameraViewOption(CameraViewsViewModel.GridCameraView, "Grid", 1, isGrid: true),
                    new CameraViewOption(CameraNames.Back, "Rear", 3),
                },
                ItemTemplate = Template("<RadioButton AutomationProperties.Name='{Binding AutomationName}' />"),
            };
            AutomationProperties.SetName(strip, "Camera views");
            Realize(strip);

            var group = UIElementAutomationPeer.CreatePeerForElement(strip);

            group.GetAutomationControlType().ShouldBe(AutomationControlType.Group);
            group.GetName().ShouldBe("Camera views");
            var tiles = group.GetChildren().ShouldNotBeNull();
            tiles.Select(tile => tile.GetAutomationControlType()).ShouldAllBe(type => type == AutomationControlType.RadioButton);
            tiles.Select(tile => tile.GetName()).ShouldBe(["Grid view", "Rear camera"]);
        });
    }

    private static DataTemplate Template(string content) =>
        (DataTemplate)XamlReader.Parse(
            $"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>{content}</DataTemplate>");

    // Items get their containers only from a template and a layout pass, which a window would supply.
    // Initializing the control the way XAML does is what gives it its default template outside any window.
    private static void Realize(ItemsControl itemsControl)
    {
        itemsControl.BeginInit();
        itemsControl.EndInit();
        itemsControl.Measure(new Size(400, 100));
        itemsControl.Arrange(new Rect(0, 0, 400, 100));
        StaThread.DrainDispatcher(itemsControl);
    }
}
