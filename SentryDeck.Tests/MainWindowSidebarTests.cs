using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;

namespace SentryDeck.Tests;

/// <summary>
/// How wide the sidebar may get beside the player, exercised on a real Grid and GridSplitter because the bug lived in how WPF lays out a pixel-wide column next to a star one.
/// </summary>
public sealed class MainWindowSidebarTests
{
    // The client area of the window at its 1200 px minimum width, inside its 8 px resize borders.
    private const double SmallestWindowWidth = 1184;
    private const double WideWindowWidth = 1600;

    // The player's width with the default sidebar at the smallest window, where the seek bar still gets about 90 px beside the fixed-width controls.
    private const double PlayerWidth = 856;

    [Fact]
    public void SidebarSplitter_DraggedAsFarAsItGoesInTheSmallestWindow_LeavesThePlayerItsWidth()
    {
        StaThread.Run(() =>
        {
            var (grid, splitter) = CreateMainContent(SmallestWindowWidth);

            DragSplitter(grid, splitter, 500);

            // The player used to drop to 736 px here, which left the seek bar no width at all.
            grid.ColumnDefinitions[2].ActualWidth.ShouldBe(PlayerWidth);
            grid.ColumnDefinitions.Sum(column => column.ActualWidth).ShouldBe(SmallestWindowWidth);
        });
    }

    [Fact]
    public void SidebarSplitter_DraggedAsFarAsItGoesInAWideWindow_StillReachesItsWidest()
    {
        StaThread.Run(() =>
        {
            var (grid, splitter) = CreateMainContent(WideWindowWidth);

            DragSplitter(grid, splitter, 500);

            grid.ColumnDefinitions[0].ActualWidth.ShouldBe(440);
        });
    }

    [Fact]
    public void SidebarColumn_WindowNarrowedAfterWideningTheSidebar_KeepsThePlayerInsideTheWindow()
    {
        StaThread.Run(() =>
        {
            var (grid, splitter) = CreateMainContent(WideWindowWidth);
            DragSplitter(grid, splitter, 500);

            Layout(grid, SmallestWindowWidth);

            // The sidebar gives way, rather than squeezing the player or pushing it past the window's edge.
            grid.ColumnDefinitions[2].ActualWidth.ShouldBe(PlayerWidth);
            grid.ColumnDefinitions.Sum(column => column.ActualWidth).ShouldBe(SmallestWindowWidth);
        });
    }

    [Fact]
    public void SidebarColumn_WindowWidenedAgain_ReturnsToTheWidthTheUserDraggedTo()
    {
        StaThread.Run(() =>
        {
            var (grid, splitter) = CreateMainContent(WideWindowWidth);
            DragSplitter(grid, splitter, 500);
            Layout(grid, SmallestWindowWidth);

            Layout(grid, WideWindowWidth);

            grid.ColumnDefinitions[0].ActualWidth.ShouldBe(440);
        });
    }

    // Mirrors the main window's columns: sidebar, splitter and player.
    private static (Grid Grid, GridSplitter Splitter) CreateMainContent(double width)
    {
        var grid = (Grid)XamlReader.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:local="clr-namespace:SentryDeck;assembly=SentryDeck"
                  x:Name="MainContent">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="320"
                                      MinWidth="260"
                                      MaxWidth="{Binding ActualWidth, ElementName=MainContent, Converter={local:RemainingWidthConverter Maximum=440, Reserved=864}}" />
                    <ColumnDefinition Width="8" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <GridSplitter Grid.Column="1"
                              Width="8"
                              HorizontalAlignment="Stretch" />
            </Grid>
            """);

        Layout(grid, width);
        return (grid, grid.Children.OfType<GridSplitter>().Single());
    }

    // The window resizing its content, with the layout passes a shown window would get.
    private static void Layout(Grid grid, double width)
    {
        grid.Measure(new Size(width, 700));
        grid.Arrange(new Rect(0, 0, width, 700));
        StaThread.DrainDispatcher(grid);
    }

    // The splitter's automation move goes through the same limits as a drag with the mouse.
    private static void DragSplitter(Grid grid, GridSplitter splitter, double horizontalChange)
    {
        var transform = (ITransformProvider)UIElementAutomationPeer.CreatePeerForElement(splitter).GetPattern(PatternInterface.Transform);
        transform.Move(horizontalChange, 0);
        StaThread.DrainDispatcher(grid);
    }
}
