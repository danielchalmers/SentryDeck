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

    /// <summary>
    /// Rough length of the footage for the clip list, known before any media is opened.
    /// A scanned clip counts its final chunk, and any chunk whose successor doesn't start about a minute later, at its recorded length; otherwise every chunk counts as <see cref="ClipTimeline.EstimatedChunkSeconds"/>.
    /// </summary>
    public TimeSpan EstimatedDuration { get; private init; }

    public CamClip(string path, string name, DateTime timestamp, IEnumerable<CamChunk> chunks, CamEvent camEvent)
    {
        FullPath = Path.GetFullPath(path);
        Name = name;
        Timestamp = timestamp;
        Chunks = chunks.ToList();
        Event = camEvent;
        ThumbnailPath = Path.Combine(FullPath, "thumb.png");
        EstimatedDuration = new ClipTimeline(Chunks).Duration;
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

        return new(directory, title, timestamp, chunks, eventData)
        {
            EstimatedDuration = EstimateDuration(chunks),
        };
    }

    /// <summary>
    /// Tesla almost always cuts a chunk short when recording stops, so counting it as a full minute made rows in the list read a minute or more longer than the player.
    /// That happens to the last chunk and to chunks around a gap in the recording, and a next chunk that doesn't start about a minute later is the only sign of a gap the file names give.
    /// Reading only those chunks' headers keeps the scan to a few reads per clip, since the other chunks are nearly always full minutes.
    /// The spacing alone can't stand in for a chunk's length: the car's clock can jump, so a full chunk can be followed by the next one a few seconds later.
    /// </summary>
    private static TimeSpan EstimateDuration(IReadOnlyList<CamChunk> chunks)
    {
        var nominalChunk = TimeSpan.FromSeconds(ClipTimeline.EstimatedChunkSeconds);
        var total = TimeSpan.Zero;

        for (var i = 0; i < chunks.Count; i++)
        {
            var ranFullMinute = i + 1 < chunks.Count
                && (chunks[i + 1].Timestamp - chunks[i].Timestamp - nominalChunk).Duration() <= RegularChunkSpacingTolerance;
            total += ranFullMinute ? nominalChunk : RecordedLengthOrNominal(chunks[i], nominalChunk);
        }

        return total;
    }

    /// <summary>
    /// Consecutive full chunks start 60 to 62 s apart on real drives, so a next chunk within a couple of seconds of a minute later means this one ran its full length.
    /// </summary>
    private static readonly TimeSpan RegularChunkSpacingTolerance = TimeSpan.FromSeconds(2);

    private static TimeSpan RecordedLengthOrNominal(CamChunk chunk, TimeSpan nominalChunk)
    {
        if (chunk.Files.TryGetValue(CameraNames.Front, out var front)
            && Mp4DurationReader.TryReadDuration(front.FullPath) is { } recorded
            && recorded > TimeSpan.Zero)
        {
            // Capped at the nominal length so a corrupt header claiming hours can only shorten the estimate, never inflate it.
            return recorded < nominalChunk ? recorded : nominalChunk;
        }

        return nominalChunk;
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

    /// <summary>
    /// Walks the tree depth-first, reading one folder's subfolders at a time and closing it before going further.
    /// The built-in recursive enumeration went breadth-first and opened a handle to every subfolder it queued, so a SavedClips folder with thousands of events held thousands of handles at once.
    /// </summary>
    private static IEnumerable<string> EnumerateClipCandidates(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.TryPop(out var directory))
        {
            yield return directory;

            List<string> subfolders;
            try
            {
                subfolders = ListSubfolders(directory);
            }
            catch (DirectoryNotFoundException) when (directory != rootDirectory)
            {
                // Deleted or moved since its parent was read, for example a clip removed in Explorer mid-scan.
                // The built-in recursion skipped these too, and the folders still to come are worth scanning.
                continue;
            }
            catch (Exception ex)
            {
                // Any other failure (a corrupt directory on the car's USB drive, a folder swapped for a file, a junction loop) costs only what is beneath this folder.
                // Depth-first order lists each event folder before reaching its later siblings, so ending the walk here would hide most of a SavedClips folder, not just the few folders the old breadth-first scan lost.
                Log.Warning(ex, "Could not list a clip folder's subfolders; skipping them. Root={Root} Folder={Folder}", rootDirectory, directory);
                continue;
            }

            // Reversed so they come off the stack in the order the file system listed them.
            for (var i = subfolders.Count - 1; i >= 0; i--)
            {
                pending.Push(subfolders[i]);
            }
        }
    }

    // IgnoreInaccessible so one ACL-denied subfolder is skipped rather than aborting the entire scan.
    // It also keeps the scan out of the hidden "Application Data" style junctions Windows leaves in every profile: they deny listing, and some point back at their own parent folder.
    // AttributesToSkip defaults to Hidden | System, which silently dropped every clip under a SavedClips or SentryClips folder that a copy or repair tool had marked that way.
    private static readonly EnumerationOptions SubfolderListingOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Reads the whole listing before returning, so the folder's handle is closed before the walk goes deeper instead of staying open for every level above the current one.
    /// </summary>
    private static List<string> ListSubfolders(string directory)
        => new FileSystemEnumerable<string>(directory, static (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(), SubfolderListingOptions)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory && !IsHousekeepingFolder(entry.FileName),
        }.ToList();

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
