using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Builds per-camera ffconcat playlists on disk so a whole clip can be opened by FFmpeg's
/// concat demuxer in one go.
/// </summary>
public partial class FfconcatMediaSourceBuilder : IClipMediaSourceBuilder
{
    private static readonly string PlaylistDirectory =
        Path.Combine(Path.GetTempPath(), "SentryReplay", "playlists");

    public ClipMediaSource Build(CamClip clip, IReadOnlySet<int> excludedChunkIndices = null)
    {
        ArgumentNullException.ThrowIfNull(clip);

        Directory.CreateDirectory(PlaylistDirectory);

        // The remaining chunks, in original order, with their original clip index preserved so
        // callers can map a timeline position back to a chunk in the source clip. A chunk whose
        // front file has no readable duration (no valid moov, e.g. a truncated recording) is
        // unplayable by FFmpeg and can crash the concat demuxer, so it is auto-excluded up front
        // for all cameras, exactly like a caller-supplied exclusion.
        var includedIndices = new List<int>();
        var chunkDurations = new List<TimeSpan>();
        var autoExcludedIndices = new List<int>();

        for (var index = 0; index < clip.Chunks.Count; index++)
        {
            if (excludedChunkIndices is not null && excludedChunkIndices.Contains(index))
            {
                continue;
            }

            var chunkDuration = ProbeFrontChunkDuration(clip.Chunks[index]);
            if (chunkDuration is null)
            {
                autoExcludedIndices.Add(index);
                continue;
            }

            includedIndices.Add(index);
            chunkDurations.Add(chunkDuration.Value);
        }

        var chunkStarts = new List<TimeSpan>(chunkDurations.Count);
        var runningStart = TimeSpan.Zero;
        foreach (var chunkDuration in chunkDurations)
        {
            chunkStarts.Add(runningStart);
            runningStart += chunkDuration;
        }

        var playlistPaths = new Dictionary<string, string>();
        var clipToken = SanitizeForFileName(clip.Name);

        foreach (var camera in CameraNames.All)
        {
            if (includedIndices.Count == 0 || !clip.Chunks[includedIndices[0]].Files.ContainsKey(camera))
            {
                continue;
            }

            var entries = new List<(string FilePath, TimeSpan Duration)>();
            for (var i = 0; i < includedIndices.Count; i++)
            {
                if (!clip.Chunks[includedIndices[i]].Files.TryGetValue(camera, out var file))
                {
                    break;
                }

                // An unreadable side file is treated exactly like a missing one: this camera's
                // playlist truncates here. The shared timeline is unaffected -- it is driven by
                // the front camera, whose readability was already verified above.
                if (camera != CameraNames.Front && !IsProbeable(file.FullPath))
                {
                    Log.Warning(
                        "Side camera file has no readable duration (likely corrupt/truncated); truncating that camera's playlist. Camera={Camera}; File={File}",
                        camera,
                        file.FullPath);
                    break;
                }

                entries.Add((file.FullPath, chunkDurations[i]));
            }

            var playlistPath = Path.Combine(PlaylistDirectory, $"{clipToken}-{camera}.ffconcat");
            WritePlaylist(playlistPath, entries);
            playlistPaths[camera] = playlistPath;
        }

        var duration = chunkStarts.Count == 0
            ? TimeSpan.Zero
            : chunkStarts[^1] + chunkDurations[^1];

        var chunkTimestamps = includedIndices.Select(index => clip.Chunks[index].Timestamp).ToList();

        return new ClipMediaSource(duration, chunkStarts, playlistPaths, autoExcludedIndices, chunkTimestamps, chunkDurations);
    }

    private static TimeSpan? ProbeFrontChunkDuration(CamChunk chunk)
    {
        if (!chunk.Files.TryGetValue(CameraNames.Front, out var frontFile))
        {
            Log.Warning(
                "Chunk has no front camera file; excluding chunk. ChunkTimestamp={ChunkTimestamp}",
                chunk.Timestamp);
            return null;
        }

        var probed = Mp4DurationReader.TryReadDuration(frontFile.FullPath);
        if (probed is { } duration && duration > TimeSpan.Zero)
        {
            return duration;
        }

        Log.Warning(
            "Front camera file has no readable duration (likely corrupt/truncated); excluding chunk. ChunkTimestamp={ChunkTimestamp}; File={File}",
            chunk.Timestamp,
            frontFile.FullPath);
        return null;
    }

    private static bool IsProbeable(string path)
    {
        return Mp4DurationReader.TryReadDuration(path) is { } duration && duration > TimeSpan.Zero;
    }

    private static void WritePlaylist(string path, IReadOnlyList<(string FilePath, TimeSpan Duration)> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ffconcat version 1.0");

        foreach (var (filePath, duration) in entries)
        {
            builder.Append("file '").Append(EscapeConcatPath(filePath)).AppendLine("'");
            builder.Append("duration ").AppendLine(duration.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace('\\', '/').Replace("'", "'\\''");
    }

    private static string SanitizeForFileName(string name)
    {
        var sanitized = InvalidFileNameCharsRegex().Replace(name ?? string.Empty, "_");
        return string.IsNullOrEmpty(sanitized) ? "clip" : sanitized;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_-]+")]
    private static partial Regex InvalidFileNameCharsRegex();
}
