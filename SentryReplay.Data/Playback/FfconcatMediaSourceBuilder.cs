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
    private const double FallbackChunkSeconds = 60;

    private static readonly string PlaylistDirectory =
        Path.Combine(Path.GetTempPath(), "SentryReplay", "playlists");

    public ClipMediaSource Build(CamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        Directory.CreateDirectory(PlaylistDirectory);

        var chunkDurations = clip.Chunks
            .Select(chunk => ProbeChunkDuration(chunk))
            .ToList();

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
            if (clip.Chunks.Count == 0 || !clip.Chunks[0].Files.ContainsKey(camera))
            {
                continue;
            }

            var entries = new List<(string FilePath, TimeSpan Duration)>();
            for (var i = 0; i < clip.Chunks.Count; i++)
            {
                if (!clip.Chunks[i].Files.TryGetValue(camera, out var file))
                {
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

        return new ClipMediaSource(duration, chunkStarts, playlistPaths);
    }

    private static TimeSpan ProbeChunkDuration(CamChunk chunk)
    {
        if (chunk.Files.TryGetValue(CameraNames.Front, out var frontFile))
        {
            var probed = Mp4DurationReader.TryReadDuration(frontFile.FullPath);
            if (probed is { } duration && duration > TimeSpan.Zero)
            {
                return duration;
            }

            Log.Debug(
                "Falling back to estimated chunk duration. ChunkTimestamp={ChunkTimestamp}; File={File}",
                chunk.Timestamp,
                frontFile.FullPath);
        }

        return TimeSpan.FromSeconds(FallbackChunkSeconds);
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
