namespace SentryDeck.Tests;

public sealed class SeekScrubCoalescerTests
{
    [Fact]
    public void FirstValue_IssuesImmediately()
    {
        var issued = new List<TimeSpan>();
        var coalescer = new SeekScrubCoalescer(position =>
        {
            issued.Add(position);
            return Task.CompletedTask;
        });

        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(1));

        issued.ShouldBe([TimeSpan.FromSeconds(1)]);
    }

    [Fact]
    public async Task RapidBurst_WhileSeekInFlight_DropsIntermediatesAndIssuesOnlyLastValueOnCompletion()
    {
        var issued = new List<TimeSpan>();
        var gate = new TaskCompletionSource();
        var coalescer = new SeekScrubCoalescer(async position =>
        {
            issued.Add(position);

            // The first seek blocks so the burst below all lands while it's in flight.
            if (issued.Count == 1)
            {
                await gate.Task;
            }
        });

        // First value issues immediately and blocks on the gate.
        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(1));
        issued.ShouldBe([TimeSpan.FromSeconds(1)]);

        // A rapid burst of further values arrives while the first seek is still in flight; all but
        // the last are dropped entirely (never issued, not even queued).
        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(1.3));
        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(1.6));
        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(1.9));
        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(2.5));

        issued.ShouldBe([TimeSpan.FromSeconds(1)]); // still only the first -- nothing issued yet

        // Let the first seek complete; the coalescer should now issue exactly the last queued value.
        gate.SetResult();
        await WaitUntilAsync(() => issued.Count == 2);

        issued.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)]);
    }

    [Fact]
    public void ValueWithinMinimumStep_OfLastIssuedValue_IsSkipped()
    {
        var issued = new List<TimeSpan>();
        var coalescer = new SeekScrubCoalescer(
            position =>
            {
                issued.Add(position);
                return Task.CompletedTask;
            },
            minimumStep: TimeSpan.FromMilliseconds(250));

        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(10));
        issued.ShouldBe([TimeSpan.FromSeconds(10)]);

        // Well within 250ms of the last-issued value -- skipped, no new seek.
        coalescer.OnDragValueChanged(TimeSpan.FromMilliseconds(10100));
        issued.ShouldBe([TimeSpan.FromSeconds(10)]);

        // Clears the 250ms threshold -- issued.
        coalescer.OnDragValueChanged(TimeSpan.FromMilliseconds(10300));
        issued.ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10300)]);
    }

    [Fact]
    public async Task PendingValueWithinMinimumStep_OfLastIssuedValue_IsNotReissuedOnCompletion()
    {
        var issued = new List<TimeSpan>();
        var gate = new TaskCompletionSource();
        var coalescer = new SeekScrubCoalescer(async position =>
        {
            issued.Add(position);
            if (issued.Count == 1)
            {
                await gate.Task;
            }
        });

        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(5));

        // Queued while in flight, but only 100ms away from the last-issued value.
        coalescer.OnDragValueChanged(TimeSpan.FromMilliseconds(5100));

        gate.SetResult();

        // Give the continuation a chance to run; it must NOT issue a second seek.
        await Task.Delay(50);

        issued.ShouldBe([TimeSpan.FromSeconds(5)]);
        coalescer.IsSeekInFlight.ShouldBeFalse();
    }

    [Fact]
    public void Reset_ForgetsLastIssuedValue_SoNextValueAlwaysIssues()
    {
        var issued = new List<TimeSpan>();
        var coalescer = new SeekScrubCoalescer(position =>
        {
            issued.Add(position);
            return Task.CompletedTask;
        });

        coalescer.OnDragValueChanged(TimeSpan.FromSeconds(10));
        coalescer.Reset();

        // Even though this is well within 250ms of the previous value, Reset cleared history for
        // the new drag, so it must issue.
        coalescer.OnDragValueChanged(TimeSpan.FromMilliseconds(10050));

        issued.ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10050)]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
