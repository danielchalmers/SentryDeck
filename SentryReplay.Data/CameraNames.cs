namespace SentryReplay;

/// <summary>
/// Camera names used by Tesla dashcam file names.
/// </summary>
public static class CameraNames
{
    public const string Front = "front";
    public const string Back = "back";
    public const string LeftRepeater = "left_repeater";
    public const string RightRepeater = "right_repeater";

    public static IReadOnlyList<string> All { get; } =
    [
        Front,
        Back,
        LeftRepeater,
        RightRepeater,
    ];

    /// <summary>
    /// Friendly label for a camera name, e.g. "left_repeater" -> "left repeater".
    /// </summary>
    public static string DisplayName(string camera) => camera?.Replace('_', ' ') ?? string.Empty;
}
