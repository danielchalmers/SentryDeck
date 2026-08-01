using System.Runtime.CompilerServices;

namespace SentryDeck.Tests;

internal static class Wait
{
    /// <summary>
    /// Polls until the condition holds, then throws naming the predicate that never came true.
    /// These tests drive the most timing-sensitive code in the suite, so a hang has to say what it was waiting for rather than surfacing as a bare cancellation.
    /// </summary>
    /// <remarks>
    /// The deadline is deliberately generous: callers wait on real clip opens flowing through Task.Run and the media source builder, which slow down a lot under the CPU and disk contention of the full suite's parallel test classes.
    /// A long deadline costs nothing when the condition comes true, and it only bounds how quickly a genuine hang surfaces; CI additionally caps the whole run with --blame-hang.
    /// </remarks>
    public static async Task UntilAsync(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string description = null)
    {
        var timeout = TimeSpan.FromSeconds(20);
        var deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met within {timeout}: {description}");
            }

            await Task.Delay(10);
        }
    }
}
