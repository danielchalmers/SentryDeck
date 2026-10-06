using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace SentryDeck.Tests;

/// <summary>
/// The clip list's scrolling and highlight, exercised on a real grouped, virtualizing ListBox because what they guard against is WPF leaving a selection off-screen or unhighlighted.
/// </summary>
public sealed class MainWindowClipListTests
{
    private const double ListHeight = 200;

    [Fact]
    public void KeepSelectionInView_RowSelectedFarBelowTheView_ScrollsItIntoView()
    {
        StaThread.Run(() =>
        {
            var rows = CreateRows(120);
            var list = CreateGroupedList(rows);
            MainWindow.KeepSelectionInView(list);

            // Next and Previous select through the view-model, the way this assignment does, not by a click on the row.
            list.SelectedItem = rows[100];
            StaThread.DrainDispatcher(list);

            IsFullyVisible(list, rows[100]).ShouldBeTrue();
        });
    }

    [Fact]
    public void KeepSelectionInView_RowSelectedAboveTheView_ScrollsBackUpToIt()
    {
        StaThread.Run(() =>
        {
            var rows = CreateRows(120);
            var list = CreateGroupedList(rows);
            MainWindow.KeepSelectionInView(list);

            // The user scrolled down to browse older days.
            Descendants(list).OfType<ScrollViewer>().First().ScrollToEnd();
            StaThread.DrainDispatcher(list);

            list.SelectedItem = rows[3];
            StaThread.DrainDispatcher(list);

            IsFullyVisible(list, rows[3]).ShouldBeTrue();
        });
    }

    [Fact]
    public void HighlightRowWhenDeselectIsTurnedDown_DeselectTheSourceRefuses_HighlightsTheRowAgain()
    {
        StaThread.Run(() =>
        {
            var rows = CreateRows(20);
            var list = CreateGroupedList(rows);
            var selection = new SelectionSource { RefusesDeselect = true };
            BindSelection(list, selection);
            MainWindow.HighlightRowWhenDeselectIsTurnedDown(list);
            list.SelectedItem = rows[2];
            StaThread.DrainDispatcher(list);

            // A Ctrl+click on the selected row unselects its container, as this does.
            Container(list, rows[2]).IsSelected = false;
            StaThread.DrainDispatcher(list);

            // The view-model kept the open clip selected, but the row it plays from had lost its highlight.
            selection.Selected.ShouldBe(rows[2]);
            Container(list, rows[2]).IsSelected.ShouldBeTrue();
            list.SelectedIndex.ShouldBe(2);
        });
    }

    [Fact]
    public void HighlightRowWhenDeselectIsTurnedDown_DeselectTheSourceAccepts_LeavesTheRowDeselected()
    {
        StaThread.Run(() =>
        {
            var rows = CreateRows(20);
            var list = CreateGroupedList(rows);
            var selection = new SelectionSource();
            BindSelection(list, selection);
            MainWindow.HighlightRowWhenDeselectIsTurnedDown(list);
            list.SelectedItem = rows[2];
            StaThread.DrainDispatcher(list);

            Container(list, rows[2]).IsSelected = false;
            StaThread.DrainDispatcher(list);

            selection.Selected.ShouldBeNull();
            Container(list, rows[2]).IsSelected.ShouldBeFalse();
            list.SelectedItem.ShouldBeNull();
        });
    }

    private static void BindSelection(ListBox list, SelectionSource selection) =>
        list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(SelectionSource.Selected)) { Source = selection, Mode = BindingMode.TwoWay });

    private static ListBoxItem Container(ListBox list, Row row) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);

    private static List<Row> CreateRows(int count) => [.. Enumerable.Range(0, count).Select(index => new Row(index))];

    // Mirrors the clip list: rows grouped by day, virtualized even while grouping, with recycled containers.
    private static ListBox CreateGroupedList(List<Row> rows)
    {
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Day)));

        var list = new ListBox();

        // Initializing it the way XAML does is what gives a list outside any window its default template, and with it the ScrollViewer that does the scrolling.
        list.BeginInit();
        list.ItemsSource = view;
        list.GroupStyle.Add(new GroupStyle());
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.EndInit();

        list.Measure(new Size(200, ListHeight));
        list.Arrange(new Rect(0, 0, 200, ListHeight));
        list.UpdateLayout();
        return list;
    }

    private static bool IsFullyVisible(ListBox list, Row row)
    {
        var scrollViewer = Descendants(list).OfType<ScrollViewer>().First();
        var container = Descendants(list).OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.DataContext, row));
        if (container is null)
        {
            return false;
        }

        var bounds = container.TransformToAncestor(scrollViewer).TransformBounds(new Rect(container.RenderSize));
        return bounds.Top >= 0 && bounds.Bottom <= scrollViewer.ActualHeight;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;

            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    // Stands in for the clip library's list selection, which turns down a deselect of the clip the player has open.
    private sealed class SelectionSource : INotifyPropertyChanged
    {
        private object _selected;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool RefusesDeselect { get; init; }

        public object Selected
        {
            get => _selected;
            set
            {
                if (value is null && RefusesDeselect)
                {
                    return;
                }

                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }

    private sealed record Row(int Index)
    {
        // A handful of rows per group, like several clips on one day.
        public int Day => Index / 8;
    }
}
