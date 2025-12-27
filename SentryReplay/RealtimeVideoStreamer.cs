using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;
using SentryReplay.Data;

namespace SentryReplay;

/// <summary>
/// Provides real-time video streaming with GPU acceleration for instant playback
/// and accurate seeking. Uses ffmpeg to stream directly without pre-rendering.
/// </summary>
public sealed class RealtimeVideoStreamer : IDisposable
{
    private static readonly object GpuLock = new();
    private static Task<GpuCapabilities> CachedGpuTask;

    private readonly VideoStreamServer _streamServer;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private bool _isDisposed;
    private string _currentStreamUrl;
    private readonly string _tempDir;

    private CamClip _concatClip;
    private Dictionary<string, string> _concatFiles;

    // Camera layout settings (Tesla-style)
    private const int MainWidth = 1280;
    private const int MainHeight = 960;
    private const int OverlayWidth = 280;
    private const int OverlayHeight = 210;
    private const int OverlayPadding = 15;

    public RealtimeVideoStreamer()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SentryReplay-Stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _streamServer = new VideoStreamServer();
        _streamServer.StreamError += (_, msg) => StreamError?.Invoke(this, msg);
        _streamServer.StreamStarted += (_, _) => StreamStarted?.Invoke(this, EventArgs.Empty);
        _streamServer.StreamEnded += (_, _) => StreamEnded?.Invoke(this, EventArgs.Empty);
        _streamServer.FfmpegLogLine += (_, line) => OnFFmpegOutput(line);
    }

    public CamClip CurrentClip { get; private set; }
    public TimeSpan CurrentPosition { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsStreaming => _streamServer?.IsStreaming ?? false;
    public string StreamUrl => _currentStreamUrl;
    public string OutputPath => _currentStreamUrl;

    public event EventHandler StreamStarted;
    public event EventHandler StreamEnded;
    public event EventHandler<string> StreamError;
    public event EventHandler<TimeSpan> PositionChanged;

    /// <summary>
    /// Detects available GPU hardware acceleration.
    /// </summary>
    public static Task<GpuCapabilities> DetectGpuCapabilitiesAsync()
    {
        lock (GpuLock)
        {
            CachedGpuTask ??= DetectGpuCapabilitiesCoreAsync();
            return CachedGpuTask;
        }
    }

    private static async Task<GpuCapabilities> DetectGpuCapabilitiesCoreAsync()
    {
        var capabilities = new GpuCapabilities();

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-hwaccels",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var fullOutput = output + error;

            capabilities.HasCuda = fullOutput.Contains("cuda");
            capabilities.HasD3D11VA = fullOutput.Contains("d3d11va");
            capabilities.HasDXVA2 = fullOutput.Contains("dxva2");
            capabilities.HasQSV = fullOutput.Contains("qsv");
            capabilities.HasVulkan = fullOutput.Contains("vulkan");

            // Check for NVENC encoder
            var encoderProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-encoders",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            encoderProcess.Start();
            var encoderOutput = await encoderProcess.StandardOutput.ReadToEndAsync();
            var encoderError = await encoderProcess.StandardError.ReadToEndAsync();
            await encoderProcess.WaitForExitAsync();

            var encoderFullOutput = encoderOutput + encoderError;
            capabilities.HasNvenc = encoderFullOutput.Contains("h264_nvenc");
            capabilities.HasAmf = encoderFullOutput.Contains("h264_amf");
            capabilities.HasQsvEnc = encoderFullOutput.Contains("h264_qsv");

            Log.Information($"GPU Capabilities: CUDA={capabilities.HasCuda}, NVENC={capabilities.HasNvenc}, " +
                          $"D3D11VA={capabilities.HasD3D11VA}, QSV={capabilities.HasQSV}, AMF={capabilities.HasAmf}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to detect GPU capabilities");
        }

        return capabilities;
    }

    /// <summary>
    /// Starts streaming the specified clip, optionally seeking to a position.
    /// This method is nearly instant as it doesn't pre-render.
    /// </summary>
    public async Task<string> StartStreamAsync(CamClip clip, TimeSpan? seekPosition = null, CancellationToken cancellationToken = default)
    {
        await _streamLock.WaitAsync(cancellationToken);

        try
        {
            // Stop any existing stream
            await StopStreamInternalAsync();

            CurrentClip = clip;
            CurrentPosition = seekPosition ?? TimeSpan.Zero;
            Duration = CalculateClipDuration(clip);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Build concat lists for each camera (cached per clip to make seeks fast)
            var concatFiles = await BuildConcatFilesAsync(clip, _cts.Token);
            if (!concatFiles.ContainsKey("front"))
            {
                StreamError?.Invoke(this, "No front camera footage found");
                return null;
            }

            // Detect GPU capabilities once (cached). Not currently used for args, but kept for future.
            _ = await DetectGpuCapabilitiesAsync();

            // Build and start ffmpeg process (streamed via local HTTP server)
            var args = BuildStreamingArgs(concatFiles, CurrentPosition);

            _streamServer.Start();
            _streamServer.SetSource(args);

            _currentStreamUrl = $"{_streamServer.StreamUri}?v={Guid.NewGuid():N}";
            return _currentStreamUrl;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start stream");
            StreamError?.Invoke(this, ex.Message);
            return null;
        }
        finally
        {
            _streamLock.Release();
        }
    }

    /// <summary>
    /// Seeks to a specific position in the current clip.
    /// This restarts the ffmpeg process at the new position for accurate seeking.
    /// </summary>
    public async Task<string> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (CurrentClip is null)
            return null;

        // Clamp position to valid range
        position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(position.TotalSeconds, Duration.TotalSeconds)));

        Log.Debug($"Seeking to {position}");
        return await StartStreamAsync(CurrentClip, position, cancellationToken);
    }

    /// <summary>
    /// Stops the current stream.
    /// </summary>
    public async Task StopStreamAsync()
    {
        await _streamLock.WaitAsync();
        try
        {
            await StopStreamInternalAsync();
        }
        finally
        {
            _streamLock.Release();
        }
    }

    private async Task StopStreamInternalAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try
        {
            _streamServer?.ClearSource();
        }
        catch
        {
        }

        _currentStreamUrl = null;
        await Task.CompletedTask;
    }

    private async Task<Dictionary<string, string>> BuildConcatFilesAsync(CamClip clip, CancellationToken ct)
    {
        if (_concatClip == clip && _concatFiles is not null && _concatFiles.Count > 0)
        {
            return _concatFiles;
        }

        var cameras = new[] { "front", "back", "left_repeater", "right_repeater" };
        var concatFiles = new Dictionary<string, string>();

        foreach (var camera in cameras)
        {
            var sb = new StringBuilder();
            foreach (var chunk in clip.Chunks)
            {
                var file = chunk.Files.GetValueOrDefault(camera);
                if (file is not null && File.Exists(file.FullPath))
                {
                    var escapedPath = file.FullPath.Replace("\\", "/").Replace("'", "'\\''");
                    sb.AppendLine($"file '{escapedPath}'");
                }
            }

            if (sb.Length > 0)
            {
                var concatPath = Path.Combine(_tempDir, $"{camera}.txt");
                await File.WriteAllTextAsync(concatPath, sb.ToString(), ct);
                concatFiles[camera] = concatPath;
            }
        }

        _concatClip = clip;
        _concatFiles = concatFiles;
        return concatFiles;
    }

    private string BuildStreamingArgs(Dictionary<string, string> concatFiles, TimeSpan seekPosition)
    {
        var sb = new StringBuilder();
        sb.Append("-y ");

        // Seek position (before inputs for fast seeking)
        if (seekPosition > TimeSpan.Zero)
        {
            sb.Append($"-ss {seekPosition:hh\\:mm\\:ss\\.fff} ");
        }

        // Hardware acceleration (safe mode)
        // Using hwaccel_output_format (d3d11/cuda surfaces) breaks the filter graph on many systems.
        // Keep this aligned with the known-good ClipRenderer pipeline.
        sb.Append("-hwaccel auto ");

        // Add inputs for each camera
        var inputIndex = 0;
        var cameraInputs = new Dictionary<string, int>();

        foreach (var kvp in concatFiles)
        {
            sb.Append($"-f concat -safe 0 -i \"{kvp.Value}\" ");
            cameraInputs[kvp.Key] = inputIndex++;
        }

        // Build filter complex for multi-camera composite
        var filters = BuildFilterComplex(cameraInputs);
        var finalOutput = filters.LastOutput;

        sb.Append($"-filter_complex \"{filters.FilterString}\" ");
        sb.Append($"-map \"{finalOutput}\" ");

        // Encoding settings
        // Prefer stability: FFME needs a consistently decodable stream.
        // If we re-enable GPU encoders later, do it behind a setting + validation.
        sb.Append("-c:v libx264 -preset ultrafast -crf 28 -tune zerolatency ");

        // Fragmented MP4 for progressive open while writing
        sb.Append("-f mp4 -movflags +frag_keyframe+empty_moov+default_base_moof ");
        sb.Append("-an "); // No audio
        sb.Append("-threads 0 ");
        sb.Append("-");

        return sb.ToString();
    }

    private (string FilterString, string LastOutput) BuildFilterComplex(Dictionary<string, int> cameraInputs)
    {
        var filters = new List<string>();
        var frontInput = cameraInputs["front"];

        // Scale front camera to base resolution
        filters.Add($"[{frontInput}:v]scale={MainWidth}:{MainHeight},setsar=1[main]");

        var currentOutput = "[main]";
        var overlayIndex = 0;

        // Overlay positions
        var positions = new (string camera, string x, string y)[]
        {
            ("back", $"W-{OverlayWidth}-{OverlayPadding}", $"{OverlayPadding}"),
            ("left_repeater", $"{OverlayPadding}", $"H-{OverlayHeight}-{OverlayPadding}"),
            ("right_repeater", $"W-{OverlayWidth}-{OverlayPadding}", $"H-{OverlayHeight}-{OverlayPadding}"),
        };

        foreach (var (camera, x, y) in positions)
        {
            if (!cameraInputs.TryGetValue(camera, out var camInput))
                continue;

            var scaled = $"s{overlayIndex}";
            var output = $"o{overlayIndex}";

            filters.Add($"[{camInput}:v]scale={OverlayWidth}:{OverlayHeight},setsar=1[{scaled}]");
            filters.Add($"{currentOutput}[{scaled}]overlay={x}:{y}[{output}]");

            currentOutput = $"[{output}]";
            overlayIndex++;
        }

        return (string.Join(";", filters), currentOutput);
    }

    private static TimeSpan CalculateClipDuration(CamClip clip)
    {
        // Estimate ~60 seconds per chunk
        return TimeSpan.FromSeconds(clip.Chunks.Count * 60);
    }

    private void OnFFmpegOutput(string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        // Log errors
        if (data.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"[ffmpeg] {data}");
        }

        // Parse time progress for position tracking
        if (data.Contains("time="))
        {
            var match = System.Text.RegularExpressions.Regex.Match(data, @"time=(\d+):(\d+):(\d+)\.(\d+)");
            if (match.Success)
            {
                var hours = int.Parse(match.Groups[1].Value);
                var minutes = int.Parse(match.Groups[2].Value);
                var seconds = int.Parse(match.Groups[3].Value);
                var ms = int.Parse(match.Groups[4].Value) * 10;

                var elapsed = new TimeSpan(0, hours, minutes, seconds, ms);
                var actualPosition = CurrentPosition + elapsed;

                if (Math.Abs((actualPosition - CurrentPosition).TotalSeconds) > 0.5)
                {
                    CurrentPosition = actualPosition;
                    PositionChanged?.Invoke(this, CurrentPosition);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        try
        {
            _cts?.Cancel();
            _streamServer?.Dispose();
        }
        catch { }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }

        _streamLock?.Dispose();
        _cts?.Dispose();
    }
}
