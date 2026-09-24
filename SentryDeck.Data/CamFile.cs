using System.Globalization;
using System.Text.RegularExpressions;

namespace SentryDeck;

/// <summary>
/// A playable dashcam media file for one camera angle.
/// </summary>
public partial record class CamFile
{
    /// <summary>
    /// Full path to the media file.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// Timestamp parsed from the TeslaCam file name.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// Camera name parsed from the TeslaCam file name.
    /// </summary>
    public string Camera { get; private init; }

    /// <summary>
    /// Zero for an original recording; N for a numbered copy such as <c>...-front-2.mp4</c>, which appears when a drive's contents are copied or merged onto another.
    /// </summary>
    public int CopyNumber { get; private init; }

    public CamFile(string path, DateTime timestamp, string camera, int copyNumber = 0)
    {
        FullPath = Path.GetFullPath(path);
        Timestamp = timestamp;
        Camera = camera;
        CopyNumber = copyNumber;
    }

    /// <summary>
    /// Finds valid TeslaCam media files in a single directory.
    /// </summary>
    public static IEnumerable<CamFile> FindFiles(string rootDirectory)
    {
        return Directory.EnumerateFiles(rootDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(TryMap)
            .OfType<CamFile>()
            .OrderBy(file => file.Timestamp)
            .ThenBy(file => file.Camera);
    }

    public override string ToString() => $"{Camera}";

    private static CamFile TryMap(string path)
    {
        var match = FileNameRegex().Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return null;
        }

        // The regex only checks digit counts, so a pattern-valid but calendar-invalid name (e.g. month 13) reaches here; TryParseExact skips it instead of throwing and aborting the whole scan.
        if (!DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return null;
        }

        // Canonicalize legacy aliases (e.g. rear_view -> back) so old and new clips share one camera vocabulary.
        // An unrecognized camera name (a future camera) is kept as-is rather than dropped.
        var camera = CameraNames.Canonicalize(match.Groups["camera"].Value.ToLowerInvariant());
        var copyNumber = match.Groups["copy"].Success ? int.Parse(match.Groups["copy"].Value, CultureInfo.InvariantCulture) : 0;
        return new CamFile(path, timestamp, camera, copyNumber);
    }

    // Anchored on both ends: an unanchored match picked up macOS "._" resource-fork files and names like "x.mp4.tmp.mp4", and on NTFS the "._" twin sorts first and displaced the real video.
    // The camera is a plain identifier followed by an optional "-N" copy suffix, so "front-2.mp4" is a copy of front rather than a camera called "front-2".
    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})-(?<camera>[a-z][a-z0-9_]*?)(?:-(?<copy>\d{1,3}))?\.mp4$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}
