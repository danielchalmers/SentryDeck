using Flyleaf.FFmpeg;
using FlyleafLib;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Starts Flyleaf with the bundled FFmpeg binaries.
/// </summary>
public sealed class FlyleafRuntime
{
    private bool _isStarted;

    public bool IsStarted => _isStarted;

    public bool TryStart()
    {
        if (_isStarted)
        {
            return true;
        }

        var directory = PackageManager.FindFFmpegDirectory();
        if (directory is null)
        {
            Log.Warning("FFmpeg binaries were not found");
            return false;
        }

        try
        {
            Engine.Start(new EngineConfig
            {
                FFmpegPath = directory,
                FFmpegLoadProfile = LoadProfile.Main,
                FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warn,
                LogLevel = FlyleafLib.LogLevel.Warn,
                LogOutput = ":debug",
                UIRefresh = true,
            });

            _isStarted = true;
            Log.Information("Started Flyleaf. FFmpegDirectory={FFmpegDirectory}", directory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start Flyleaf. FFmpegDirectory={FFmpegDirectory}", directory);
            return false;
        }
    }
}
