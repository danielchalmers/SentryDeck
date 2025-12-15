using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;
using TeslaCam.Data;

namespace TeslaCam;

/// <summary>
/// Renders a single clip's video chunks into a combined video using ffmpeg.
/// Composites all camera angles (front, back, left, right) into a Tesla-style layout.
/// </summary>
public sealed class ClipRenderer : IDisposable
{
    private Process _ffmpegProcess;
    private CancellationTokenSource _cts;
    private bool _isDisposed;
    private readonly string _outputPath;
    private readonly string _tempDir;
    private TimeSpan _estimatedDuration;

    // Camera layout settings (Tesla-style)
    private const int OverlayWidth = 280;
    private const int OverlayHeight = 210;
    private const int OverlayPadding = 15;

    public ClipRenderer(CamClip clip)
    {
        Clip = clip;
        var id = Guid.NewGuid().ToString("N")[..8];
        _tempDir = Path.Combine(Path.GetTempPath(), $"TeslaCam-{id}");
        _outputPath = Path.Combine(Path.GetTempPath(), $"TeslaCam-{id}.mp4");
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
    /// Starts rendering the clip with all camera angles composited.
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
            Log.Warning($"Clip already rendering: {Clip.Name}");
            return false;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _estimatedDuration = TimeSpan.FromSeconds(Clip.Chunks.Count * 60); // ~1 min per chunk

        try
        {
            Directory.CreateDirectory(_tempDir);

            // Build concat file lists for each camera
            var cameras = new[] { "front", "back", "left_repeater", "right_repeater" };
            var concatFiles = new Dictionary<string, string>();
            var availableCameras = new List<string>();

            foreach (var camera in cameras)
            {
                var concatList = BuildConcatList(camera);
                if (!string.IsNullOrWhiteSpace(concatList))
                {
                    var concatPath = Path.Combine(_tempDir, $"{camera}.txt");
                    await File.WriteAllTextAsync(concatPath, concatList, _cts.Token);
                    concatFiles[camera] = concatPath;
                    availableCameras.Add(camera);
                }
            }

            if (!concatFiles.ContainsKey("front"))
            {
                Log.Error("No front camera footage found");
                RenderFailed?.Invoke(this, "No front camera footage found");
                return false;
            }

            Log.Information($"Rendering {Clip.Name} with cameras: {string.Join(", ", availableCameras)}");

            var args = BuildFFmpegArgs(concatFiles, availableCameras);

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

            _ffmpegProcess.ErrorDataReceived += OnFFmpegOutput;

            Log.Debug($"ffmpeg {args}");
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();

            await _ffmpegProcess.WaitForExitAsync(_cts.Token);

            var exitCode = _ffmpegProcess.ExitCode;

            if (exitCode == 0 && IsRendered)
            {
                RenderProgress = 1.0;
                ProgressChanged?.Invoke(this, RenderProgress);
                RenderCompleted?.Invoke(this, EventArgs.Empty);
                Log.Information($"Render completed: {Clip.Name}");
                return true;
            }
            else
            {
                Log.Error($"Render failed with exit code {exitCode}");
                RenderFailed?.Invoke(this, $"FFmpeg failed (code {exitCode})");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"Render cancelled: {Clip.Name}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Render error: {Clip.Name}");
            RenderFailed?.Invoke(this, ex.Message);
            return false;
        }
        finally
        {
            await CleanupProcessAsync();
        }
    }

    private void OnFFmpegOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;

        // Only log errors and important info, not every frame
        if (e.Data.Contains("Error") || e.Data.Contains("error"))
        {
            Log.Warning($"[ffmpeg] {e.Data}");
        }

        // Parse time progress
        if (e.Data.Contains("time=") && _estimatedDuration.TotalSeconds > 0)
        {
            var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+)");
            if (match.Success)
            {
                var hours = int.Parse(match.Groups[1].Value);
                var minutes = int.Parse(match.Groups[2].Value);
                var seconds = int.Parse(match.Groups[3].Value);
                var elapsed = new TimeSpan(hours, minutes, seconds);

                var progress = Math.Min(0.99, elapsed.TotalSeconds / _estimatedDuration.TotalSeconds);
                if (Math.Abs(progress - RenderProgress) > 0.01) // Only update if changed significantly
                {
                    RenderProgress = progress;
                    ProgressChanged?.Invoke(this, RenderProgress);
                }
            }
        }
    }

    private async Task CleanupProcessAsync()
    {
        if (_ffmpegProcess is not null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                    await Task.Run(() => _ffmpegProcess.WaitForExit(3000));
                }
            }
            catch { }

            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        // Wait for file handles to release
        await Task.Delay(200);

        // Clean up concat list files only
        TryDeleteDirectory(_tempDir);
    }

    /// <summary>
    /// Builds ffmpeg arguments for multi-camera composite - optimized for speed.
    /// Uses hardware acceleration when available.
    /// </summary>
    private string BuildFFmpegArgs(Dictionary<string, string> concatFiles, List<string> availableCameras)
    {
        var sb = new StringBuilder();
        sb.Append("-y ");
        
        // Use hardware acceleration if available (will fallback gracefully)
        sb.Append("-hwaccel auto ");

        // Add inputs for each camera
        var inputIndex = 0;
        var cameraInputs = new Dictionary<string, int>();

        foreach (var camera in availableCameras)
        {
            sb.Append($"-f concat -safe 0 -i \"{concatFiles[camera]}\" ");
            cameraInputs[camera] = inputIndex++;
        }

        // Build filter complex
        var filters = new List<string>();
        var frontInput = cameraInputs["front"];

        // Scale front camera to base resolution
        filters.Add($"[{frontInput}:v]scale=1280:960,setsar=1[main]");

        var currentOutput = "[main]";
        var overlayIndex = 0;

        // Overlay positions
        var positions = new (string camera, string x, string y, string label)[]
        {
            ("back", $"W-{OverlayWidth}-{OverlayPadding}", $"{OverlayPadding}", "Back"),
            ("left_repeater", $"{OverlayPadding}", $"H-{OverlayHeight}-{OverlayPadding}", "Left"),
            ("right_repeater", $"W-{OverlayWidth}-{OverlayPadding}", $"H-{OverlayHeight}-{OverlayPadding}", "Right"),
        };

        foreach (var (camera, x, y, label) in positions)
        {
            if (!cameraInputs.TryGetValue(camera, out var camInput))
                continue;

            var scaled = $"s{overlayIndex}";
            var output = $"o{overlayIndex}";

            // Scale overlay camera (no labels to save time)
            filters.Add($"[{camInput}:v]scale={OverlayWidth}:{OverlayHeight},setsar=1[{scaled}]");
            filters.Add($"{currentOutput}[{scaled}]overlay={x}:{y}[{output}]");

            currentOutput = $"[{output}]";
            overlayIndex++;
        }

        // Finalize output (remove front label for speed)
        if (currentOutput != "[main]")
        {
            // We have overlays, use last output
            sb.Append($"-filter_complex \"{string.Join(";", filters)}\" ");
            sb.Append($"-map \"{currentOutput}\" ");
        }
        else
        {
            // Only front camera
            sb.Append($"-filter_complex \"{string.Join(";", filters)}\" ");
            sb.Append("-map \"[main]\" ");
        }

        // Super fast encoding - prioritize speed over quality
        // Use veryfast instead of ultrafast for slightly better quality at similar speed
        sb.Append("-c:v libx264 -preset veryfast -crf 26 ");
        sb.Append("-movflags +faststart "); // Enable fast start for streaming
        sb.Append("-an "); // No audio for now (faster)
        sb.Append("-threads 0 "); // Auto thread count
        sb.Append($"\"{_outputPath}\"");

        return sb.ToString();
    }

    private string BuildConcatList(string camera)
    {
        var sb = new StringBuilder();

        foreach (var chunk in Clip.Chunks)
        {
            var file = chunk.Files.GetValueOrDefault(camera);
            if (file is not null && File.Exists(file.FullPath))
            {
                var escapedPath = file.FullPath.Replace("\\", "/").Replace("'", "'\\''");
                sb.AppendLine($"file '{escapedPath}'");
            }
        }

        return sb.ToString();
    }

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
            catch { }
        }
    }

    public void Cleanup()
    {
        CancelRender();
        TryDeleteFile(_outputPath);
        TryDeleteDirectory(_tempDir);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try { File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        try { Directory.Delete(path, true); }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Cleanup();
        _cts?.Dispose();
    }
}
