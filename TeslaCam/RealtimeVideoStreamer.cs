using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Serilog;
using TeslaCam.Data;

namespace TeslaCam;

/// <summary>
/// Provides real-time video streaming with GPU acceleration for instant playback
/// and accurate seeking. Uses ffmpeg to stream directly without pre-rendering.
/// </summary>
public sealed class RealtimeVideoStreamer : IDisposable
{
    private Process _ffmpegProcess;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private bool _isDisposed;
    private string _currentOutputPath;
    private readonly string _tempDir;

    // Camera layout settings (Tesla-style)
    private const int MainWidth = 1280;
    private const int MainHeight = 960;
    private const int OverlayWidth = 280;
    private const int OverlayHeight = 210;
    private const int OverlayPadding = 15;

    public RealtimeVideoStreamer()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TeslaCam-Stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public CamClip CurrentClip { get; private set; }
    public TimeSpan CurrentPosition { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsStreaming => _ffmpegProcess is not null && !_ffmpegProcess.HasExited;
    public string OutputPath => _currentOutputPath;

    public event EventHandler StreamStarted;
    public event EventHandler StreamEnded;
    public event EventHandler<string> StreamError;
    public event EventHandler<TimeSpan> PositionChanged;

    /// <summary>
    /// Detects available GPU hardware acceleration.
    /// </summary>
    public static async Task<GpuCapabilities> DetectGpuCapabilitiesAsync()
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

            // Create output file path
            _currentOutputPath = Path.Combine(_tempDir, $"stream_{Guid.NewGuid():N}.mp4");

            // Build concat lists for each camera
            var concatFiles = await BuildConcatFilesAsync(clip, _cts.Token);
            if (!concatFiles.ContainsKey("front"))
            {
                StreamError?.Invoke(this, "No front camera footage found");
                return null;
            }

            // Detect GPU capabilities for optimal encoding
            var gpu = await DetectGpuCapabilitiesAsync();

            // Build and start ffmpeg process
            var args = BuildStreamingArgs(concatFiles, gpu, CurrentPosition);

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
            _ffmpegProcess.Exited += OnFFmpegExited;

            Log.Debug($"Starting stream: ffmpeg {args}");
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();

            // Wait a brief moment for ffmpeg to start writing
            await WaitForOutputFileAsync(_currentOutputPath, TimeSpan.FromSeconds(3), _cts.Token);

            StreamStarted?.Invoke(this, EventArgs.Empty);

            return _currentOutputPath;
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
        if (_ffmpegProcess is not null)
        {
            try
            {
                _cts?.Cancel();

                if (!_ffmpegProcess.HasExited)
                {
                    // Send 'q' to gracefully stop ffmpeg
                    try
                    {
                        _ffmpegProcess.Kill();
                    }
                    catch { }

                    await Task.Run(() => _ffmpegProcess.WaitForExit(2000));
                }
            }
            catch { }
            finally
            {
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
            }

            StreamEnded?.Invoke(this, EventArgs.Empty);
        }

        // Clean up old output file
        await Task.Delay(100); // Wait for file handles
        TryDeleteFile(_currentOutputPath);
        _currentOutputPath = null;
    }

    private async Task<Dictionary<string, string>> BuildConcatFilesAsync(CamClip clip, CancellationToken ct)
    {
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

        return concatFiles;
    }

    private string BuildStreamingArgs(Dictionary<string, string> concatFiles, GpuCapabilities gpu, TimeSpan seekPosition)
    {
        var sb = new StringBuilder();
        sb.Append("-y ");

        // Seek position (before inputs for fast seeking)
        if (seekPosition > TimeSpan.Zero)
        {
            sb.Append($"-ss {seekPosition:hh\\:mm\\:ss\\.fff} ");
        }

        // Hardware acceleration for decoding
        if (gpu.HasD3D11VA)
        {
            sb.Append("-hwaccel d3d11va -hwaccel_output_format d3d11 ");
        }
        else if (gpu.HasCuda)
        {
            sb.Append("-hwaccel cuda -hwaccel_output_format cuda ");
        }
        else if (gpu.HasDXVA2)
        {
            sb.Append("-hwaccel dxva2 ");
        }

        // Add inputs for each camera
        var inputIndex = 0;
        var cameraInputs = new Dictionary<string, int>();
        var availableCameras = new List<string>();

        foreach (var kvp in concatFiles)
        {
            sb.Append($"-f concat -safe 0 -i \"{kvp.Value}\" ");
            cameraInputs[kvp.Key] = inputIndex++;
            availableCameras.Add(kvp.Key);
        }

        // Build filter complex for multi-camera composite
        var filters = BuildFilterComplex(cameraInputs, availableCameras);
        var finalOutput = filters.LastOutput;

        sb.Append($"-filter_complex \"{filters.FilterString}\" ");
        sb.Append($"-map \"{finalOutput}\" ");

        // Encoding settings - use GPU if available, otherwise fast CPU
        if (gpu.HasNvenc)
        {
            sb.Append("-c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0 ");
        }
        else if (gpu.HasAmf)
        {
            sb.Append("-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 ");
        }
        else if (gpu.HasQsvEnc)
        {
            sb.Append("-c:v h264_qsv -preset veryfast -global_quality 28 ");
        }
        else
        {
            // Fast CPU encoding
            sb.Append("-c:v libx264 -preset ultrafast -crf 28 -tune zerolatency ");
        }

        sb.Append("-movflags +frag_keyframe+empty_moov+faststart ");
        sb.Append("-an "); // No audio
        sb.Append("-threads 0 ");
        sb.Append($"\"{_currentOutputPath}\"");

        return sb.ToString();
    }

    private (string FilterString, string LastOutput) BuildFilterComplex(Dictionary<string, int> cameraInputs, List<string> availableCameras)
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

    private async Task WaitForOutputFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (fi.Length > 1024) // Wait for some data
                {
                    return;
                }
            }

            await Task.Delay(50, ct);
        }
    }

    private void OnFFmpegOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;

        // Log errors
        if (e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"[ffmpeg] {e.Data}");
        }

        // Parse time progress for position tracking
        if (e.Data.Contains("time="))
        {
            var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+)\.(\d+)");
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

    private void OnFFmpegExited(object sender, EventArgs e)
    {
        Log.Debug($"FFmpeg process exited with code: {_ffmpegProcess?.ExitCode}");
        StreamEnded?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try { File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts?.Cancel();
            _ffmpegProcess?.Kill();
            _ffmpegProcess?.Dispose();
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

/// <summary>
/// Represents GPU capabilities for hardware-accelerated video processing.
/// </summary>
public class GpuCapabilities
{
    public bool HasCuda { get; set; }
    public bool HasD3D11VA { get; set; }
    public bool HasDXVA2 { get; set; }
    public bool HasQSV { get; set; }
    public bool HasVulkan { get; set; }
    public bool HasNvenc { get; set; }
    public bool HasAmf { get; set; }
    public bool HasQsvEnc { get; set; }

    public bool HasAnyGpuDecoding => HasCuda || HasD3D11VA || HasDXVA2 || HasQSV || HasVulkan;
    public bool HasAnyGpuEncoding => HasNvenc || HasAmf || HasQsvEnc;

    public string BestDecoder
    {
        get
        {
            if (HasD3D11VA) return "d3d11va";
            if (HasCuda) return "cuda";
            if (HasDXVA2) return "dxva2";
            if (HasQSV) return "qsv";
            return "auto";
        }
    }

    public string BestEncoder
    {
        get
        {
            if (HasNvenc) return "h264_nvenc";
            if (HasAmf) return "h264_amf";
            if (HasQsvEnc) return "h264_qsv";
            return "libx264";
        }
    }
}
