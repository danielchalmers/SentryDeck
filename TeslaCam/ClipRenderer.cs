using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;
using TeslaCam.Data;

namespace TeslaCam;

/// <summary>
/// Renders a single clip's video chunks into a combined video using ffmpeg.
/// Uses the concat demuxer for reliable multi-chunk playback.
/// </summary>
public sealed class ClipRenderer : IDisposable
{
    private Process _ffmpegProcess;
    private CancellationTokenSource _cts;
    private bool _isDisposed;
    private readonly string _outputPath;
    private readonly string _concatListPath;

    public ClipRenderer(CamClip clip)
    {
        Clip = clip;
        var id = Guid.NewGuid().ToString("N")[..8];
        _outputPath = Path.Combine(Path.GetTempPath(), $"TeslaCam-{id}.mp4");
        _concatListPath = Path.Combine(Path.GetTempPath(), $"TeslaCam-{id}.txt");
    }

    public CamClip Clip { get; }

    public string OutputPath => _outputPath;

    public bool IsRendered => File.Exists(_outputPath) && new FileInfo(_outputPath).Length > 1024;

    public bool IsRendering => _ffmpegProcess is not null && !_ffmpegProcess.HasExited;

    public double RenderProgress { get; private set; }

    public event EventHandler<double> ProgressChanged;
    public event EventHandler RenderCompleted;
    public event EventHandler<string> RenderFailed;

    /// <summary>
    /// Starts rendering the clip to a temporary file for playback.
    /// Uses concat demuxer for reliable chunk concatenation.
    /// </summary>
    public async Task<bool> RenderAsync(CancellationToken cancellationToken = default)
    {
        if (IsRendered)
        {
            Log.Debug($"Clip already rendered: {Clip.Name}");
            return true;
        }

        if (IsRendering)
        {
            Log.Warning($"Clip is already being rendered: {Clip.Name}");
            return false;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Build concat file list for the primary camera (front)
            var concatList = BuildConcatList("front");
            
            if (string.IsNullOrEmpty(concatList))
            {
                RenderFailed?.Invoke(this, "No valid video files found");
                return false;
            }

            // Write concat list to temp file
            await File.WriteAllTextAsync(_concatListPath, concatList, _cts.Token);
            Log.Debug($"Concat list:\n{concatList}");

            // Simple ffmpeg concat command - just concatenate the front camera clips
            var args = $"-y -f concat -safe 0 -i \"{_concatListPath}\" -c copy \"{_outputPath}\"";

            _ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true
            };

            var totalDuration = TimeSpan.FromMinutes(Clip.Chunks.Count); // Estimate ~1 min per chunk

            _ffmpegProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;

                Log.Verbose($"[ffmpeg] {e.Data}");

                // Parse time progress
                if (e.Data.Contains("time="))
                {
                    var timeMatch = System.Text.RegularExpressions.Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+)\.(\d+)");
                    if (timeMatch.Success)
                    {
                        var hours = int.Parse(timeMatch.Groups[1].Value);
                        var minutes = int.Parse(timeMatch.Groups[2].Value);
                        var seconds = int.Parse(timeMatch.Groups[3].Value);
                        var elapsed = new TimeSpan(hours, minutes, seconds);

                        if (totalDuration.TotalSeconds > 0)
                        {
                            RenderProgress = Math.Min(0.99, elapsed.TotalSeconds / totalDuration.TotalSeconds);
                            ProgressChanged?.Invoke(this, RenderProgress);
                        }
                    }
                }
            };

            Log.Information($"Starting render: ffmpeg {args}");
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();

            await _ffmpegProcess.WaitForExitAsync(_cts.Token);

            var exitCode = _ffmpegProcess.ExitCode;
            Log.Debug($"FFmpeg exited with code {exitCode}");

            if (exitCode == 0 && IsRendered)
            {
                RenderProgress = 1.0;
                ProgressChanged?.Invoke(this, RenderProgress);
                RenderCompleted?.Invoke(this, EventArgs.Empty);
                Log.Information($"Render completed: {Clip.Name} -> {_outputPath}");
                return true;
            }
            else
            {
                var error = $"FFmpeg exited with code {exitCode}";
                RenderFailed?.Invoke(this, error);
                Log.Error($"Render failed: {error}");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information($"Render cancelled: {Clip.Name}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Render failed: {Clip.Name}");
            RenderFailed?.Invoke(this, ex.Message);
            return false;
        }
        finally
        {
            _ffmpegProcess?.Dispose();
            _ffmpegProcess = null;
            
            // Clean up concat list file
            TryDeleteFile(_concatListPath);
        }
    }

    /// <summary>
    /// Builds the concat demuxer file list for the specified camera.
    /// </summary>
    private string BuildConcatList(string camera)
    {
        var sb = new StringBuilder();

        foreach (var chunk in Clip.Chunks)
        {
            var file = chunk.Files.GetValueOrDefault(camera);
            if (file is not null && File.Exists(file.FullPath))
            {
                // Escape single quotes and backslashes for ffmpeg concat format
                var escapedPath = file.FullPath.Replace("\\", "/").Replace("'", "'\\''");
                sb.AppendLine($"file '{escapedPath}'");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Cancels any ongoing render operation.
    /// </summary>
    public void CancelRender()
    {
        _cts?.Cancel();

        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error killing ffmpeg process");
            }
        }
    }

    /// <summary>
    /// Deletes the rendered output file.
    /// </summary>
    public void Cleanup()
    {
        CancelRender();
        TryDeleteFile(_outputPath);
        TryDeleteFile(_concatListPath);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"Failed to delete temp file: {path}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Cleanup();
        _cts?.Dispose();
    }
}
