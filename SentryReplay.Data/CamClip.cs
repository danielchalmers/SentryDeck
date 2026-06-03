using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SentryReplay;

/// <summary>
/// A folder containing a collection of <see cref="CamChunk"/>s that make up a single continuous dashcam clip.
/// </summary>
public partial record class CamClip
{
    /// <summary>
    /// The path to the directory containing all the media files and metadata for this clip.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// The title from the folder name or timestamp.
    /// </summary>
    public string Name { get; private init; }

    /// <summary>
    /// The timestamp parsed from the folder name title if it's available.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// The ordered list of chunks that make up the clip as a whole.
    /// </summary>
    public LinkedList<CamChunk> Chunks { get; private init; }

    /// <summary>
    /// The event data associated with this clip.
    /// </summary>
    public CamEvent Event { get; private init; }

    /// <summary>
    /// The path to the thumbnail image for this clip.
    /// </summary>
    public string ThumbnailPath { get; private init; }

    public CamClip(string path, string name, DateTime timestamp, LinkedList<CamChunk> chunks, CamEvent camEvent)
    {
        FullPath = Path.GetFullPath(path);
        Name = name;
        Timestamp = timestamp;
        Chunks = chunks;
        Event = camEvent;
        ThumbnailPath = Path.Combine(FullPath, "thumb.png");
    }

    public static CamClip Map(string directory)
    {
        var eventData = CamEvent.FromFile(Path.Combine(directory, "event.json"));
        var title = Path.GetFileName(directory);
        DateTime timestamp = default;

        // If the folder name is in the default format we can parse the date to make it look better; otherwise we use the renamed title.
        var match = FolderNameRegex().Match(title);
        if (match.Success)
        {
            timestamp = DateTime.ParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            title = timestamp.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            // We can try to get the timestamp from the event data if the folder name is missing it.
            if (eventData is not null && eventData.Timestamp != default)
            {
                timestamp = eventData.Timestamp;
            }
        }

        var chunks = CamChunk.Map(directory);

        if (chunks.Count == 0)
        {
            // Folder does not contain any valid chunks and serves no purpose.
            return null;
        }

        return new(directory, title, timestamp, chunks, eventData);
    }

    /// <summary>
    /// Finds all the clip folders inside the specified root directory.
    /// </summary>
    public static IEnumerable<CamClip> FindClips(string rootDirectory)
    {
        var directories = Directory.GetDirectories(rootDirectory, "*", SearchOption.AllDirectories);

        foreach (var directory in directories)
        {
            var clip = Map(directory);

            if (clip is not null)
            {
                yield return clip;
            }
        }
    }

    public string Summary
    {
        get
        {
            var builder = new StringBuilder();

            builder.Append(Name);

            if (Event?.City is not null)
            {
                builder.AppendLine();
                builder.Append(Event.City);
            }

            return builder.ToString();
        }
    }

    public override string ToString() => $"{Name}";

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})")]
    private static partial Regex FolderNameRegex();
}
