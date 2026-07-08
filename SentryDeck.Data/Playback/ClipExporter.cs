using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;

namespace SentryDeck;

/// <summary>
/// A single export job: trim the clip's media timeline to [<see cref="Start"/>, <see cref="End"/>)
/// for one camera and write the result to <see cref="OutputPath"/>. Times are media time on the
/// opened <see cref="MediaSource"/> (the seek-bar axis), not wall-clock time.
/// </summary>
public sealed record class ClipExportRequest(
    CamClip Clip,
    ClipMediaSource MediaSource,
    string Camera,
    TimeSpan Start,
    TimeSpan End,
    string OutputPath);

/// <summary>
/// Exports a trimmed range of a clip.
/// </summary>
public interface IClipExporter
{
    Task ExportAsync(ClipExportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exports a media-time range of one camera's footage as a single mp4 via FFmpeg's concat
/// demuxer with per-file inpoint/outpoint directives and stream copy — no re-encode, so exports
/// are fast and lossless. Stream copy cuts at keyframes, so the actual bounds can land up to a
/// GOP (~1s in Tesla footage) before the requested ones.
/// </summary>
public sealed class ClipExporter(Func<string> ffmpegDirectoryResolver) : IClipExporter
{
    private static readonly string ExportScriptDirectory =
        Path.Combine(Path.GetTempPath(), "SentryDeck", "exports");

    public async Task ExportAsync(ClipExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ffmpegDirectory = ffmpegDirectoryResolver()
            ?? throw new InvalidOperationException("FFmpeg is not installed. Restart the app to download it.");
        var ffmpegPath = Path.Combine(ffmpegDirectory, "ffmpeg.exe");

        var entries = ResolveEntries(request);

        Directory.CreateDirectory(ExportScriptDirectory);

        // Unique per export (unlike the deterministic playback playlists): two exports of the
        // same clip may overlap in time and must not clobber each other's scripts.
        var scriptPath = Path.Combine(ExportScriptDirectory, $"{Guid.NewGuid():N}.ffconcat");
        await File.WriteAllTextAsync(scriptPath, BuildConcatScript(entries), cancellationToken);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await RunFfmpegAsync(ffmpegPath, BuildArguments(scriptPath, request.OutputPath), cancellationToken);
            Log.Information(
                "Exported clip range. Clip={ClipName}; Camera={Camera}; Start={Start}; End={End}; Output={Output}; ElapsedMs={ElapsedMs}",
                request.Clip.Name,
                request.Camera,
                request.Start,
                request.End,
                request.OutputPath,
                stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            // Don't leave a broken half-written file where the user asked to save.
            TryDelete(request.OutputPath);
            throw;
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    /// <summary>
    /// Resolves the trim segments to this camera's files. A chunk past the first missing/absent
    /// camera file truncates the export there, mirroring how playback truncates that camera's
    /// playlist; no footage at all for the range is an error.
    /// </summary>
    internal static IReadOnlyList<(string FilePath, TimeSpan? InPoint, TimeSpan? OutPoint)> ResolveEntries(ClipExportRequest request)
    {
        var segments = request.MediaSource.GetTrimSegments(request.Start, request.End);
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("The selected range contains no footage.");
        }

        var chunksByTimestamp = request.Clip.Chunks.ToDictionary(chunk => chunk.Timestamp);
        var entries = new List<(string FilePath, TimeSpan? InPoint, TimeSpan? OutPoint)>();

        foreach (var segment in segments)
        {
            if (!chunksByTimestamp.TryGetValue(segment.ChunkTimestamp, out var chunk)
                || !chunk.Files.TryGetValue(request.Camera, out var file))
            {
                break;
            }

            entries.Add((file.FullPath, segment.InPoint, segment.OutPoint));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException($"The {CameraNames.DisplayName(request.Camera)} camera has no footage in the selected range.");
        }

        if (entries.Count < segments.Count)
        {
            Log.Warning(
                "Export truncated at a missing camera file. Camera={Camera}; IncludedChunks={Included}; RequestedChunks={Requested}",
                request.Camera,
                entries.Count,
                segments.Count);
        }

        return entries;
    }

    internal static string BuildConcatScript(IReadOnlyList<(string FilePath, TimeSpan? InPoint, TimeSpan? OutPoint)> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ffconcat version 1.0");

        foreach (var (filePath, inPoint, outPoint) in entries)
        {
            builder.Append("file '").Append(FfconcatMediaSourceBuilder.EscapeConcatPath(filePath)).AppendLine("'");

            if (inPoint is { } trimStart)
            {
                builder.Append("inpoint ").AppendLine(trimStart.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture));
            }

            if (outPoint is { } trimEnd)
            {
                builder.Append("outpoint ").AppendLine(trimEnd.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    internal static string BuildArguments(string scriptPath, string outputPath)
    {
        // -c copy: stream copy, no re-encode. +faststart: moov up front so the export streams
        // well when shared. -safe 0: the script references absolute paths.
        return $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{scriptPath}\" -c copy -movflags +faststart \"{outputPath}\"";
    }

    private static async Task RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(ffmpegPath, arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg failed to start.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the cancellation and the kill.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = (await stderrTask).Trim();
            throw new InvalidOperationException(stderr.Length > 0 ? stderr : $"FFmpeg exited with code {process.ExitCode}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to delete export temp/partial file. Path={Path}", path);
        }
    }
}
