using System.Globalization;
using System.IO.Enumeration;
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

        // Neither the folder name nor event.json supplied a timestamp (e.g. Tesla's RecentClips folder: loose files directly under it, with no date-named subfolder and no event metadata).
        // Fall back to the earliest chunk's timestamp, parsed from the file names, so the clip sorts and dates correctly instead of collapsing to DateTime.MinValue (1/1/0001).
        if (timestamp == default)
        {
            timestamp = chunks[0].Timestamp;
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
    /// Maps one folder, skipping (and logging) any folder that can't be read so a single bad clip never discards the rest of the library.
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

    public override string ToString() => $"{Name}";

    private static IEnumerable<string> EnumerateClipCandidates(string rootDirectory)
    {
        yield return rootDirectory;

        // IgnoreInaccessible so one ACL-denied subfolder is skipped rather than aborting the entire scan.
        // It also keeps the scan out of the hidden "Application Data" style junctions Windows leaves in every profile: they deny listing, and some point back at their own parent folder.
        // AttributesToSkip defaults to Hidden | System, which silently dropped every clip under a SavedClips or SentryClips folder that a copy or repair tool had marked that way.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        IEnumerator<string> directories;
        try
        {
            directories = new FileSystemEnumerable<string>(rootDirectory, static (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(), options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory && !IsHousekeepingFolder(entry.FileName),
                ShouldRecursePredicate = static (ref FileSystemEntry entry) => !IsHousekeepingFolder(entry.FileName),
            }.GetEnumerator();
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
                    // A transient IO error partway through recursion (bad sector, a drive yanked mid-scan, a junction loop) would otherwise abort the whole root and discard every clip found so far.
                    // Stop here and keep what we already enumerated.
                    Log.Warning(ex, "Clip-folder enumeration stopped early; keeping folders found so far. Root={Root}", rootDirectory);
                    break;
                }

                yield return directory;
            }
        }
    }

    /// <summary>
    /// Windows keeps these folders at a drive root, and the Recycle Bin holds the clips the app's own Delete sent there, so scanning a drive root must not list them again as live clips.
    /// Skipping them by attribute used to do this by accident; matching the name keeps it working now that hidden and system folders are scanned.
    /// </summary>
    private static bool IsHousekeepingFolder(ReadOnlySpan<char> name)
        => name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})")]
    private static partial Regex FolderNameRegex();
}
