using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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

    [Fact]
    public void NameSearchClearButton_BoxFocused_NamesTheThemesClearButton()
    {
        StaThread.Run(() =>
        {
            var searchBox = CreateFluentTextBox();
            MainWindow.NameSearchClearButton(searchBox);

            searchBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, searchBox)
            {
                RoutedEvent = Keyboard.GotKeyboardFocusEvent,
            });

            // Finding the part by name also catches a theme update that renames it, which would silently undo the fix.
            var clearButton = searchBox.Template.FindName("DeleteButton", searchBox).ShouldBeOfType<Button>();
            AutomationProperties.GetName(clearButton).ShouldBe("Clear search");
        });
    }

    private static KeyEventArgs PressKey(UIElement target, Key key) => KeyInput.Raise(target, key, Keyboard.PreviewKeyDownEvent);

    // The app styles its TextBoxes with WPF's Fluent theme, whose template supplies the clear button.
    private static TextBox CreateFluentTextBox()
    {
        // Touching Application registers the pack:// scheme that theme dictionaries load through, which a test process otherwise lacks.
        _ = Application.Current;

        var searchBox = new TextBox();
        searchBox.BeginInit();
        searchBox.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml"),
        });
        searchBox.EndInit();

        searchBox.ApplyTemplate().ShouldBeTrue();
        return searchBox;
    }
}
