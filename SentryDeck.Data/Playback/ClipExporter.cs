using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;

namespace SentryDeck;

/// <summary>
/// A single export job: trim the clip's media timeline to [<see cref="Start"/>, <see cref="End"/>) for one camera and write the result to <see cref="OutputPath"/>.
/// Times are media time on the opened <see cref="MediaSource"/> (the seek-bar axis), not wall-clock time.
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
/// Exports a media-time range of one camera's footage as a single mp4 via FFmpeg's concat demuxer with per-file inpoint/outpoint directives and stream copy: no re-encode, so exports are fast and lossless.
/// Stream copy cuts at keyframes, so the actual bounds can land up to a GOP (~1s in Tesla footage) before the requested ones.
/// </summary>
/// <param name="runFfmpeg">
/// Runs FFmpeg with an executable path and an argument string.
/// Defaults to launching the real process; overridable for tests, which must not spawn ffmpeg.
/// </param>
public sealed class ClipExporter(
    Func<string> ffmpegDirectoryResolver,
    Func<string, string, CancellationToken, Task> runFfmpeg = null) : IClipExporter
{
    private static readonly string ExportScriptDirectory =
        Path.Combine(Path.GetTempPath(), "SentryDeck", "exports");

    // A killed process exits within milliseconds; the cap only keeps a wedged one from holding up the app's exit.
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    // Antivirus and the search indexer often open a file the moment its writer closes it, so a delete right after FFmpeg exits can hit a sharing violation for a short while.
    // A few short waits let them finish instead of leaving a partial export or a temporary script behind.
    private const int DeleteAttempts = 10;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public async Task ExportAsync(ClipExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        runFfmpeg ??= RunFfmpegAsync;

        var ffmpegDirectory = ffmpegDirectoryResolver()
            ?? throw new InvalidOperationException("FFmpeg is not installed. Restart the app to download it.");
        var ffmpegPath = Path.Combine(ffmpegDirectory, "ffmpeg.exe");

        var entries = ResolveEntries(request);

        Directory.CreateDirectory(ExportScriptDirectory);

        // Unique per export (unlike the deterministic playback playlists): two exports of the same clip may overlap in time and must not clobber each other's scripts.
        var scriptPath = Path.Combine(ExportScriptDirectory, $"{Guid.NewGuid():N}.ffconcat");
        await File.WriteAllTextAsync(scriptPath, BuildConcatScript(entries), cancellationToken);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await runFfmpeg(ffmpegPath, BuildArguments(scriptPath, request.OutputPath), cancellationToken);
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
            await TryDeleteAsync(request.OutputPath);
            throw;
        }
        finally
        {
            await TryDeleteAsync(scriptPath);
        }
    }

    /// <summary>
    /// Resolves the trim segments to this camera's files.
    /// A chunk past the first missing or present-but-unreadable camera file truncates the export there, mirroring how playback truncates that camera's playlist; no footage at all for the range is an error.
    /// </summary>
    /// <param name="isSideFileReadable">
    /// Whether a non-front camera file has a readable duration.
    /// Defaults to the same mp4 probe the media-source builder uses; overridable for tests, which work on model-only file paths.
    /// </param>
    internal static IReadOnlyList<(string FilePath, TimeSpan? InPoint, TimeSpan? OutPoint)> ResolveEntries(
        ClipExportRequest request,
        Func<string, bool> isSideFileReadable = null)
    {
        isSideFileReadable ??= path => Mp4DurationReader.TryReadDuration(path) is { } duration && duration > TimeSpan.Zero;

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

            // A present-but-unreadable side file would make FFmpeg's concat demuxer abort the whole export, so stop here exactly like the media-source builder truncates that camera's playback playlist.
            // Front files need no probe: the timeline's segments only cover chunks whose front file was already probe-verified when the media source was built.
            if (request.Camera != CameraNames.Front && !isSideFileReadable(file.FullPath))
            {
                Log.Warning(
                    "Export truncated at an unreadable camera file. Camera={Camera}; File={File}",
                    request.Camera,
                    file.FullPath);
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
        // -c copy: stream copy, no re-encode. +faststart: moov up front so the export streams well when shared. -safe 0: the script references absolute paths.
        return $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{scriptPath}\" -c copy -movflags +faststart \"{outputPath}\"";
    }

    /// <summary>Runs FFmpeg to completion, or kills it and waits for it to exit when canceled.</summary>
    internal static async Task RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken cancellationToken)
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

            // Kill only starts the termination, and FFmpeg keeps the output and the script open until it is gone.
            // Deleting them straight away would fail, leaving an unfinished export where the user asked to save.
            // Awaited rather than blocking, because the export runs from the UI thread and a process with a write in flight to a slow drive can take a moment to exit.
            using var killWait = new CancellationTokenSource(KillTimeout);
            try
            {
                await process.WaitForExitAsync(killWait.Token);
            }
            catch (OperationCanceledException)
            {
                // Still running after the cap; the cleanup logs whatever it then can't delete.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = (await stderrTask).Trim();
            throw new InvalidOperationException(stderr.Length > 0 ? stderr : $"FFmpeg exited with code {process.ExitCode}.");
        }
    }

    // Awaited rather than sleeping, because the export runs from the UI thread.
    private static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < DeleteAttempts)
            {
                await Task.Delay(DeleteRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Failed to delete export temp/partial file. Path={Path}", path);
                return;
            }
        }
    }
}
