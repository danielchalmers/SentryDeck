using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SentryDeck.Tests;

internal static class StaThread
{
    /// <summary>
    /// Runs the action on a fresh STA thread and rethrows its failure on the test's thread.
    /// WPF controls need an STA thread, which xUnit's test threads are not.
    /// </summary>
    public static void Run(Action action)
    {
        ExceptionDispatchInfo failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
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
    }

    /// <summary>
    /// Lets the dispatcher run everything already queued (deferred callbacks and layout passes) before the test looks at the result.
    /// </summary>
    /// <remarks>
    /// The element has no window, so nothing renders it; the explicit layout pass stands in for the one a shown window would get.
    /// Several rounds let work queued by one round (a scroll that asks for a new layout) finish too.
    /// </remarks>
    public static void DrainDispatcher(UIElement element)
    {
        for (var round = 0; round < 3; round++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            element.UpdateLayout();
        }
    }
}
