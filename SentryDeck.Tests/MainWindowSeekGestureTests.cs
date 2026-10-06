using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SentryDeck.Tests;

/// <summary>
/// The seek bar's press and release wiring, exercised on a real Slider because the bug it guards against lived in WPF's event routing, not in the view-model.
/// </summary>
public sealed class MainWindowSeekGestureTests
{
    [Fact]
    public void HookSeekGesture_PressOnTheRail_StartsTheGesture()
    {
        RunOnStaThread(() =>
        {
            var slider = CreateMoveToPointSlider();
            var begun = 0;
            MainWindow.HookSeekGesture(slider, () => begun++, () => Task.CompletedTask);

            var press = RaiseLeftButton(slider, UIElement.PreviewMouseDownEvent);

            // The slider itself took the press to jump its thumb there, which is what used to hide the press from the gesture.
            press.Handled.ShouldBeTrue();
            begun.ShouldBe(1);
        });
    }

    [Fact]
    public void HookSeekGesture_ReleaseAfterAPressOnTheRail_EndsTheGesture()
    {
        RunOnStaThread(() =>
        {
            var slider = CreateMoveToPointSlider();
            var ended = 0;
            MainWindow.HookSeekGesture(slider, () => { }, () =>
            {
                ended++;
                return Task.CompletedTask;
            });

            RaiseLeftButton(slider, UIElement.PreviewMouseDownEvent);
            RaiseLeftButton(slider, UIElement.PreviewMouseUpEvent);

            ended.ShouldBe(1);
        });
    }

    private static Slider CreateMoveToPointSlider()
    {
        var slider = new Slider();

        // Initializing it the way XAML does is what gives a slider outside any window its default template, and with it the Track whose rail the press lands on.
        slider.BeginInit();
        slider.Minimum = 0;
        slider.Maximum = 1;
        slider.Value = 0.5;
        slider.IsMoveToPointEnabled = true;
        slider.EndInit();

        slider.ApplyTemplate();
        slider.Measure(new Size(200, 24));
        slider.Arrange(new Rect(0, 0, 200, 24));
        return slider;
    }

    private static MouseButtonEventArgs RaiseLeftButton(UIElement target, RoutedEvent routedEvent)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
        };

        target.RaiseEvent(args);
        return args;
    }

    // WPF controls need an STA thread, which xUnit's test threads are not.
    private static void RunOnStaThread(Action action)
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
}
