using System.Diagnostics;
using System.IO;
using Serilog;
using SentryReplay.Data;
using Unosquare.FFME;

namespace SentryReplay;

public class ClipStream : IAsyncDisposable
{
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"{App.AssemblyTitle}-{Random.Shared.Next(10000, 99999)}.ts");
    private Process _ffmpegProcess;
    private CancellationTokenSource _cts;

    public ClipStream(CamClip clip)
    {
        Clip = clip;
        Uri = new(_tempFilePath);
    }

    public CamClip Clip { get; }

    public Uri Uri { get; }

    public async Task<bool> StartStream()
    {
        _cts = new();

        try
        {
            var currentChunk = Clip.Chunks.First.Value;
            StartTransportStream(currentChunk);

            await Task.Delay(2000, _cts.Token); // TODO: Properly let ffmpeg buffer.

            return await WaitForTransportStream();
        }
        catch (TaskCanceledException)
        {
            Log.Error($"Canceled clip <{Clip.Name}>");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start clip");
            return false;
        }
    }

    public async Task StopStream()
    {
        _cts?.Cancel();

        if (_ffmpegProcess?.HasExited == false)
        {
            Log.Debug("Killing current ffmpeg process");
            _ffmpegProcess.Kill();
            await _ffmpegProcess.WaitForExitAsync();
        }

        _ffmpegProcess?.Dispose();
        _ffmpegProcess = null;

        try
        {
            File.Delete(_tempFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete temp file");
        }

        Log.Debug("Stopped ffmepg");
    }

    private void StartTransportStream(CamChunk chunk, string primary = "front")
    {
        if (_ffmpegProcess is not null)
            throw new InvalidOperationException("FFmpeg is already running.");

        if (chunk == null || !chunk.Files.Any())
            throw new ArgumentException("Chunk cannot be null or empty.", nameof(chunk));

        if (!File.Exists(chunk.Files[primary].FullPath))
            throw new ArgumentException("Primary camera file does not exist.", nameof(primary));

        Log.Debug($"Primary camera: {primary}");

        // Common settings
        var resolution = "256x192";
        var cameraPadding = 30;

        var filterComplex = $@"
            [1:v]scale={resolution}[top_left_scaled];
            [top_left_scaled]drawtext=text='Front':x=5:y=h-25:fontsize=20:fontcolor=white[top_left_labeled];
            [2:v]scale={resolution}[top_right_scaled];
            [top_right_scaled]drawtext=text='Back':x=5:y=h-25:fontsize=20:fontcolor=white[top_right_labeled];
            [3:v]scale={resolution}[bottom_left_scaled];
            [bottom_left_scaled]drawtext=text='Left':x=5:y=h-25:fontsize=20:fontcolor=white[bottom_left_labeled];
            [4:v]scale={resolution}[bottom_right_scaled];
            [bottom_right_scaled]drawtext=text='Right':x=5:y=h-25:fontsize=20:fontcolor=white[bottom_right_labeled];

            [0:v][top_left_labeled]overlay={cameraPadding}:{cameraPadding}[top_left_overlay];
            [top_left_overlay][top_right_labeled]overlay=W-{resolution.Split('x')[0]}-{cameraPadding}:{cameraPadding}[top_right_overlay];
            [top_right_overlay][bottom_left_labeled]overlay={cameraPadding}:H-{resolution.Split('x')[1]}-{cameraPadding}[bottom_left_overlay];
            [bottom_left_overlay][bottom_right_labeled]overlay=W-{resolution.Split('x')[0]}-{cameraPadding}:H-{resolution.Split('x')[1]}-{cameraPadding}[output]";

        List<string> args =
        [
            "-y",
            "-i", $"\"{chunk.Files[primary].FullPath}\"",
        ];

        void AddCamSquare(string name)
        {
            var file = chunk.Files.GetValueOrDefault(name);

            if (file is null || file.Camera == primary)
            {
                Log.Debug($"Adding black square for {file.Camera}");
                args.Add("-f");
                args.Add("lavfi");
                args.Add("-i");
                args.Add("color=black");
            }
            else
            {
                Log.Debug($"Adding file stream for {file.Camera}");
                args.Add("-i");
                args.Add($"\"{file.FullPath}\"");
            }
        }

        AddCamSquare("front");
        AddCamSquare("back");
        AddCamSquare("left_repeater");
        AddCamSquare("right_repeater");

        args.AddRange(
        [
            "-filter_complex", $"\"{filterComplex}\"",
            "-map", "[output]",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-movflags", "+faststart",
#if DEBUG
        "-report",
#endif
            "-f", "mpegts",
            "-t", "60",
            _tempFilePath
        ]);

        _ffmpegProcess = new()
        {
            StartInfo = new()
            {
                FileName = "ffmpeg",
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        _ffmpegProcess.Exited += (sender, e) =>
        {
            Log.Debug("FFmpeg process exited");
        };

        Log.Debug($"{_ffmpegProcess.StartInfo.FileName} {_ffmpegProcess.StartInfo.Arguments}");
        _ffmpegProcess.Start();
    }

    private async Task<bool> WaitForTransportStream()
    {
        while (true)
        {
            if (File.Exists(_tempFilePath) && new FileInfo(_tempFilePath).Length > 0)
                return true;

            await Task.Delay(100, _cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        await StopStream();
    }
}
