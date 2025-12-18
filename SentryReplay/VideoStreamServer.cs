using System.Diagnostics;
using System.IO;
using System.Net;
using Serilog;

namespace SentryReplay;

/// <summary>
/// A local HTTP server that streams video from ffmpeg to the media player.
/// This provides reliable, buffered streaming with proper HTTP semantics.
/// </summary>
public sealed class VideoStreamServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private CancellationTokenSource _cts;
    private Task _serverTask;
    private Process _ffmpegProcess;
    private readonly object _lock = new();
    private string _currentSourcePath;
    private bool _isDisposed;

    public VideoStreamServer(int port = 0)
    {
        // Find an available port if 0 is specified
        if (port == 0)
        {
            port = FindAvailablePort();
        }

        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
    }

    public Uri StreamUri => new($"http://localhost:{_port}/stream");

    public bool IsRunning => _serverTask is not null && !_serverTask.IsCompleted;

    public event EventHandler<string> StreamError;
    public event EventHandler StreamStarted;
    public event EventHandler StreamEnded;

    private static int FindAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Start()
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();
        _listener.Start();
        _serverTask = Task.Run(() => ServerLoop(_cts.Token));
        Log.Information($"Video stream server started on port {_port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        StopFFmpeg();

        try
        {
            _listener.Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping HTTP listener");
        }

        _serverTask = null;
        Log.Information("Video stream server stopped");
    }

    public void SetSource(string ffmpegArgs)
    {
        lock (_lock)
        {
            StopFFmpeg();
            _currentSourcePath = ffmpegArgs;
            StartFFmpeg(ffmpegArgs);
        }
    }

    public void ClearSource()
    {
        lock (_lock)
        {
            StopFFmpeg();
            _currentSourcePath = null;
        }
    }

    private void StartFFmpeg(string inputArgs)
    {
        if (string.IsNullOrEmpty(inputArgs))
            return;

        var args = $"{inputArgs} -c:v libx264 -preset ultrafast -tune zerolatency -movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 -";

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true
        };

        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Log.Verbose($"[ffmpeg] {e.Data}");
            }
        };

        _ffmpegProcess.Exited += (_, _) =>
        {
            Log.Debug("FFmpeg process exited");
            StreamEnded?.Invoke(this, EventArgs.Empty);
        };

        Log.Debug($"Starting ffmpeg: {_ffmpegProcess.StartInfo.Arguments}");
        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();
        StreamStarted?.Invoke(this, EventArgs.Empty);
    }

    private void StopFFmpeg()
    {
        if (_ffmpegProcess is null)
            return;

        try
        {
            if (!_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit(1000);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping ffmpeg process");
        }
        finally
        {
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }
    }

    private async Task ServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(context, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in HTTP server loop");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;

        try
        {
            if (context.Request.Url?.AbsolutePath != "/stream")
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            Process ffmpeg;
            lock (_lock)
            {
                ffmpeg = _ffmpegProcess;
            }

            if (ffmpeg is null || ffmpeg.HasExited)
            {
                response.StatusCode = 503;
                response.Close();
                return;
            }

            response.ContentType = "video/mp4";
            response.SendChunked = true;
            response.AddHeader("Cache-Control", "no-cache, no-store");
            response.AddHeader("Connection", "close");

            var buffer = new byte[64 * 1024]; // 64KB buffer
            var stdout = ffmpeg.StandardOutput.BaseStream;

            while (!ct.IsCancellationRequested && !ffmpeg.HasExited)
            {
                var bytesRead = await stdout.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                    break;

                await response.OutputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                await response.OutputStream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (HttpListenerException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling stream request");
            StreamError?.Invoke(this, ex.Message);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Ignore close errors
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Stop();
        _cts?.Dispose();
        _listener.Close();
    }
}
