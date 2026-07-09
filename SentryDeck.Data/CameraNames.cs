namespace SentryDeck;

/// <summary>
/// Camera names used by Tesla dashcam file names.
/// </summary>
/// <remarks>
/// The camera set of an actual clip is discovered from its files, not assumed from this list:
/// newer HW4/AI4 vehicles add the two B-pillar cameras, and future firmware may add more. An
/// unrecognized suffix is kept and played rather than dropped; <see cref="All"/> is only the set
/// of cameras we recognize by name.
/// </remarks>
public static class CameraNames
{
    public const string Front = "front";
    public const string Back = "back";
    public const string LeftRepeater = "left_repeater";
    public const string RightRepeater = "right_repeater";

    /// <summary>Left B-pillar camera (HW4/AI4 only, added ~2025).</summary>
    public const string LeftPillar = "left_pillar";

    /// <summary>Right B-pillar camera (HW4/AI4 only, added ~2025).</summary>
    public const string RightPillar = "right_pillar";

    /// <summary>Legacy rear-camera suffix from old firmware; canonicalized to <see cref="Back"/>.</summary>
    public const string BackLegacy = "rear_view";

    /// <summary>The camera suffixes we recognize by name (four classic + two HW4 B-pillars).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Front,
        Back,
        LeftRepeater,
        RightRepeater,
        LeftPillar,
        RightPillar,
    ];

    /// <summary>
    /// Normalizes legacy suffix aliases so old and new clips share one vocabulary
    /// (<c>rear_view</c> → <see cref="Back"/>). Unknown suffixes pass through unchanged.
    /// </summary>
    public static string Canonicalize(string camera) => camera == BackLegacy ? Back : camera;

    /// <summary>
    /// Friendly label for a camera name, e.g. "left_repeater" -> "left repeater".
    /// </summary>
    public static string DisplayName(string camera) => camera?.Replace('_', ' ') ?? string.Empty;
}
