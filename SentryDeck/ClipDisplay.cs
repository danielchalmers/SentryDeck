using System.Globalization;

namespace SentryDeck;

/// <summary>
/// View-side presentation helpers for clip metadata.
/// Maps Tesla's raw <see cref="CamEvent.Reason"/> strings (e.g. "user_interaction_honk", "sentry_aware_object_detection", "vehicle_auto_emergency_braking") to a small, stable set of friendly categories used by the clip card and search.
/// </summary>
public static class ClipDisplay
{
    public const string ReasonSentry = "sentry";
    public const string ReasonHonk = "honk";
    public const string ReasonAlert = "alert";
    public const string ReasonSaved = "saved";
    public const string ReasonRecent = "recent";

    /// <summary>
    /// Normalizes a clip's event reason into a stable category key.
    /// A clip with no event.json, or a blank reason, falls back to its TeslaCam folder, so a saved or Sentry clip isn't passed off as the rolling Recent buffer.
    /// </summary>
    public static string ReasonKey(CamClip clip) => ReasonKey(clip?.Event, clip?.SourceFolder ?? CamSourceFolder.Other);

    /// <summary>
    /// Normalizes a raw event reason into a stable category key.
    /// </summary>
    public static string ReasonKey(CamEvent camEvent) => ReasonKey(camEvent, CamSourceFolder.Other);

    private static string ReasonKey(CamEvent camEvent, CamSourceFolder sourceFolder)
    {
        var reason = camEvent?.Reason;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return sourceFolder switch
            {
                CamSourceFolder.SavedClips => ReasonSaved,
                CamSourceFolder.SentryClips => ReasonSentry,
                _ => ReasonRecent,
            };
        }

        reason = reason.ToLowerInvariant();

        if (reason.Contains("sentry"))
        {
            return ReasonSentry;
        }

        if (reason.Contains("honk"))
        {
            return ReasonHonk;
        }

        if (reason.Contains("emergency") || reason.Contains("braking") || reason.Contains("collision"))
        {
            return ReasonAlert;
        }

        // Everything else the driver triggered (manual save, dashcam tap, etc.).
        return ReasonSaved;
    }

    /// <summary>
    /// A short, human-friendly label for the clip's event reason, or for its TeslaCam folder when the reason is missing.
    /// </summary>
    public static string ReasonLabel(CamClip clip) => LabelFor(ReasonKey(clip));

    /// <summary>
    /// A short, human-friendly label for the event reason.
    /// </summary>
    public static string ReasonLabel(CamEvent camEvent) => LabelFor(ReasonKey(camEvent));

    private static string LabelFor(string reasonKey) => reasonKey switch
    {
        ReasonSentry => "Sentry",
        ReasonHonk => "Honk",
        ReasonAlert => "Alert",
        ReasonSaved => "Saved",
        _ => "Recent",
    };

    /// <summary>
    /// True when the event carries usable coordinates for a map lookup.
    /// </summary>
    public static bool HasLocation(CamEvent camEvent) =>
        camEvent is not null && (camEvent.EstLat != 0 || camEvent.EstLon != 0);

    // The clip list and its search both take dates from here, so a date the user reads on a row or a day header always finds that clip.

    /// <summary>
    /// The date on a clip's row, e.g. "Tue, Dec 16".
    /// </summary>
    public static string RowDate(DateTime timestamp) => timestamp.ToString("ddd, MMM d", CultureInfo.CurrentCulture);

    /// <summary>
    /// The time on a clip's row, e.g. "3:53 PM".
    /// </summary>
    public static string RowTime(DateTime timestamp) => timestamp.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>
    /// The full date, e.g. "Tuesday, December 16, 2025", which heads every day before yesterday.
    /// </summary>
    public static string LongDate(DateTime timestamp) => timestamp.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// The header over a day's clips: "Today", "Yesterday", or the full date.
    /// </summary>
    public static string DayHeader(DateTime timestamp)
    {
        var date = timestamp.Date;
        var today = DateTime.Today;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return LongDate(date);
    }
}
