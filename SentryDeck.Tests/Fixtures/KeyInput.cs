using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SentryDeck.Tests;

internal static class KeyInput
{
    /// <summary>
    /// Raises a key event on the element the way a key press would, and returns it so the test can check whether anything handled it.
    /// </summary>
    public static KeyEventArgs Raise(UIElement target, Key key, RoutedEvent routedEvent)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new StubPresentationSource(), Environment.TickCount, key)
        {
            RoutedEvent = routedEvent,
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
