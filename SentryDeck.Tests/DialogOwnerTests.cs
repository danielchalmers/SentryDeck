using System.Windows;
using System.Windows.Interop;

namespace SentryDeck.Tests;

public sealed class DialogOwnerTests
{
    [Fact]
    public void Usable_NoWindow_ReturnsNone()
    {
        DialogOwner.Usable(null).ShouldBeNull();
    }

    [Fact]
    public void Usable_WindowNotOpenedYet_ReturnsNone()
    {
        StaThread.Run(() =>
        {
            // A file dialog throws when its owner has no window handle yet, where opening without an owner still works.
            DialogOwner.Usable(new Window()).ShouldBeNull();
        });
    }

    [Fact]
    public void Usable_OpenWindow_ReturnsIt()
    {
        StaThread.Run(() =>
        {
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();

            DialogOwner.Usable(window).ShouldBeSameAs(window);

            window.Close();
        });
    }
}
