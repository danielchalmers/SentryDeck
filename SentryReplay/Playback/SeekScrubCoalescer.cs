namespace SentryReplay;

/// <summary>
/// Coalesces a rapid stream of scrub-seek requests (e.g. from a dragged seek bar thumb) down to
/// at most one in-flight seek at a time, with trailing-edge coalescing: intermediate values that
/// arrive while a seek is in flight are dropped, and only the latest value is issued once the
/// in-flight seek completes -- and only if it differs meaningfully from the last-issued value.
/// No timers or polling; everything is driven by completion of the issued seek task.
/// </summary>
public sealed class SeekScrubCoalescer
{
    private readonly Func<TimeSpan, Task> _issueScrubSeek;
    private readonly TimeSpan _minimumStep;
    private readonly object _gate = new();
    private TimeSpan? _pendingValue;
    private TimeSpan? _lastIssuedValue;
    private bool _isSeekInFlight;

    /// <param name="issueScrubSeek">Performs the actual (fast/keyframe) seek for a given position.</param>
    /// <param name="minimumStep">
    /// A queued value within this much media time of the last-issued value is dropped rather than
    /// triggering another seek -- defaults to 250ms, matching the granularity a human drag can
    /// meaningfully perceive.
    /// </param>
    public SeekScrubCoalescer(Func<TimeSpan, Task> issueScrubSeek, TimeSpan? minimumStep = null)
    {
        _issueScrubSeek = issueScrubSeek ?? throw new ArgumentNullException(nameof(issueScrubSeek));
        _minimumStep = minimumStep ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>True while a scrub seek issued by this coalescer is in flight.</summary>
    public bool IsSeekInFlight
    {
        get { lock (_gate) { return _isSeekInFlight; } }
    }

    /// <summary>
    /// Reports the drag's latest value. If no seek is in flight, issues one immediately. If one is
    /// already in flight, remembers this value (overwriting any previously queued one) so it's
    /// issued -- if it still clears <see cref="_minimumStep"/> against the last-issued value -- once
    /// the in-flight seek completes.
    /// </summary>
    public void OnDragValueChanged(TimeSpan value)
    {
        lock (_gate)
        {
            if (_isSeekInFlight)
            {
                _pendingValue = value;
                return;
            }

            if (_lastIssuedValue is { } last && IsWithinMinimumStep(value, last))
            {
                return;
            }

            BeginSeekLocked(value);
        }
    }

    /// <summary>
    /// Resets state for a fresh drag: forgets the last-issued value so the next
    /// <see cref="OnDragValueChanged"/> call always issues, regardless of where a previous drag left off.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pendingValue = null;
            _lastIssuedValue = null;
        }
    }

    private bool IsWithinMinimumStep(TimeSpan value, TimeSpan last)
    {
        var delta = value - last;
        return delta > -_minimumStep && delta < _minimumStep;
    }

    private void BeginSeekLocked(TimeSpan value)
    {
        _isSeekInFlight = true;
        _lastIssuedValue = value;
        _pendingValue = null;

        // Fire-and-forget by design: the coalescer chains the next seek off this task's completion
        // itself (below), so callers never await individual scrub seeks.
        _ = RunSeekAsync(value);
    }

    private async Task RunSeekAsync(TimeSpan value)
    {
        try
        {
            await _issueScrubSeek(value);
        }
        finally
        {
            TimeSpan? next;
            lock (_gate)
            {
                _isSeekInFlight = false;
                next = _pendingValue;
                _pendingValue = null;
            }

            if (next is { } nextValue)
            {
                OnDragValueChanged(nextValue);
            }
        }
    }
}
