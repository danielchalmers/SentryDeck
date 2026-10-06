using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

/// <summary>
/// The search box's keyboard handling, exercised on a real TextBox because every key typed there is text unless the box gives it a meaning.
/// </summary>
public sealed class MainWindowSearchBoxTests
{
    [Fact]
    public void HookSearchEscape_EscapeWithAQuery_ClearsTheQuery()
    {
        StaThread.Run(() =>
        {
            var searchBox = new TextBox { Text = "honk" };
            var left = 0;
            MainWindow.HookSearchEscape(searchBox, new RelayCommand(() => searchBox.Text = string.Empty), () => left++);

            var escape = PressKey(searchBox, Key.Escape);

            escape.Handled.ShouldBeTrue();
            searchBox.Text.ShouldBeEmpty();
            left.ShouldBe(0);
        });
    }

    [Fact]
    public void HookSearchEscape_EscapeOnAnEmptyBox_LeavesTheBox()
    {
        StaThread.Run(() =>
        {
            var searchBox = new TextBox();
            var cleared = 0;
            var left = 0;
            MainWindow.HookSearchEscape(searchBox, new RelayCommand(() => cleared++), () => left++);

            var escape = PressKey(searchBox, Key.Escape);

            escape.Handled.ShouldBeTrue();
            left.ShouldBe(1);
            cleared.ShouldBe(0);
        });
    }

    [Fact]
    public void HookSearchEscape_EscapeAfterClearing_LeavesTheBox()
    {
        StaThread.Run(() =>
        {
            var searchBox = new TextBox { Text = "honk" };
            var left = 0;
            MainWindow.HookSearchEscape(searchBox, new RelayCommand(() => searchBox.Text = string.Empty), () => left++);

            PressKey(searchBox, Key.Escape);
            PressKey(searchBox, Key.Escape);

            left.ShouldBe(1);
        });
    }

    [Fact]
    public void HookSearchEscape_OtherKey_StaysText()
    {
        StaThread.Run(() =>
        {
            var searchBox = new TextBox { Text = "honk" };
            var cleared = 0;
            var left = 0;
            MainWindow.HookSearchEscape(searchBox, new RelayCommand(() => cleared++), () => left++);

            var space = PressKey(searchBox, Key.Space);

            space.Handled.ShouldBeFalse();
            cleared.ShouldBe(0);
            left.ShouldBe(0);
        });
    }

    private static KeyEventArgs PressKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new StubPresentationSource(), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

        target.RaiseEvent(args);
        return args;
    }

    // A key event must name the window it came from, and these tests have none.
    private sealed class StubPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null;
    }
}
