using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace SentryDeck;

/// <summary>
/// A folder containing chunks that make up one continuous dashcam clip.
/// </summary>
public partial record class CamClip
{
    /// <summary>
    /// Full path to the clip folder.
    /// </summary>
    public string FullPath { get; private init; }

    /// <summary>
    /// Display name from the folder name or parsed timestamp.
    /// </summary>
    public string Name { get; private init; }

    /// <summary>
    /// Timestamp parsed from the folder name or event metadata when available.
    /// </summary>
    public DateTime Timestamp { get; private init; }

    /// <summary>
    /// Ordered chunks in this clip.
    /// </summary>
    public IReadOnlyList<CamChunk> Chunks { get; private init; }

    /// <summary>
    /// Optional event metadata for this clip.
    /// </summary>
    public CamEvent Event { get; private init; }

    /// <summary>
    /// Path to the thumbnail image for this clip.
    /// </summary>
    public string ThumbnailPath { get; private init; }

    public CamClip(string path, string name, DateTime timestamp, IEnumerable<CamChunk> chunks, CamEvent camEvent)
    {
        FullPath = Path.GetFullPath(path);
        Name = name;
        Timestamp = timestamp;
        Chunks = chunks.ToList();
        Event = camEvent;
        ThumbnailPath = Path.Combine(FullPath, "thumb.png");
    }

    /// <summary>
    /// Maps a clip folder, or returns null when the folder has no playable chunks.
    /// </summary>
    public static CamClip Map(string directory)
    {
        var eventData = CamEvent.FromFile(Path.Combine(directory, "event.json"));
        var title = Path.GetFileName(directory);
        DateTime timestamp = default;

        var match = FolderNameRegex().Match(title);
        if (match.Success
            && DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var folderTimestamp))
        {
            timestamp = folderTimestamp;
            title = timestamp.ToString(CultureInfo.InvariantCulture);
        }
        else if (eventData?.Timestamp != default)
        {
            timestamp = eventData.Timestamp;
        }

        var chunks = CamChunk.Map(directory);
        if (chunks.Count == 0)
        {
            return null;
        }

        return new(directory, title, timestamp, chunks, eventData);
    }

    /// <summary>
    /// Finds clip folders inside the specified root directory.
    /// </summary>
    public static IEnumerable<CamClip> FindClips(string rootDirectory)
    {
        foreach (var directory in EnumerateClipCandidates(rootDirectory))
        {
            var clip = TryMap(directory);
            if (clip is not null)
            {
                yield return clip;
            }
        }
    }

    /// <summary>
    /// Maps one folder, skipping (and logging) any folder that can't be read so a single bad
    /// clip never discards the rest of the library.
    /// </summary>
    private static CamClip TryMap(string directory)
    {
        try
        {
            return Map(directory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Skipping unreadable clip folder. Folder={Folder}", directory);
            return null;
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

    private static IEnumerable<string> EnumerateClipCandidates(string rootDirectory)
    {
        yield return rootDirectory;

        // IgnoreInaccessible so one ACL-denied subfolder (e.g. System Volume Information at a
        // drive root) is skipped rather than aborting the entire scan.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        IEnumerator<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(rootDirectory, "*", options).GetEnumerator();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not enumerate clip folders. Root={Root}", rootDirectory);
            yield break;
        }

        using (directories)
        {
            while (true)
            {
                string directory;
                try
                {
                    if (!directories.MoveNext())
                    {
                        break;
                    }

                    directory = directories.Current;
                }
                catch (Exception ex)
                {
                    // A transient IO error partway through recursion (bad sector, a drive yanked
                    // mid-scan, a junction loop) would otherwise abort the whole root and discard
                    // every clip found so far. Stop here and keep what we already enumerated.
                    Log.Warning(ex, "Clip-folder enumeration stopped early; keeping folders found so far. Root={Root}", rootDirectory);
                    break;
                }

                yield return directory;
            }
        }
    }

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})")]
    private static partial Regex FolderNameRegex();
}
