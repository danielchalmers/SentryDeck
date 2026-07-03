namespace SentryReplay;

/// <summary>
/// View-side presentation helpers for clip metadata. Maps Tesla's raw
/// <see cref="CamEvent.Reason"/> strings (e.g. "user_interaction_honk",
/// "sentry_aware_object_detection", "vehicle_auto_emergency_braking") to a small,
/// stable set of friendly categories used by the clip card and search.
/// </summary>
public static class ClipDisplay
{
    public const string ReasonSentry = "sentry";
    public const string ReasonHonk = "honk";
    public const string ReasonAlert = "alert";
    public const string ReasonSaved = "saved";
    public const string ReasonRecent = "recent";

    /// <summary>
    /// Normalizes a raw event reason into a stable category key.
    /// </summary>
    public static string ReasonKey(CamEvent camEvent)
    {
        var reason = camEvent?.Reason;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ReasonRecent;
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
    /// A short, human-friendly label for the event reason.
    /// </summary>
    public static string ReasonLabel(CamEvent camEvent) => ReasonKey(camEvent) switch
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
}
