using System.Diagnostics;

namespace SentryReplay.Tests;

/// <summary>
/// Fast tests for FFmpeg availability and capabilities.
/// </summary>
public class FFmpegTests
{
    [Fact(Skip = "FFmpeg tests are currently disabled (incomplete/unreliable).")]
    public void FFmpeg_IsAvailable()
    {
        var (ExitCode, Output) = RunFFmpegCommand("-version");
        ExitCode.ShouldBe(0);
        Output.ShouldContain("ffmpeg version");
    }

    [Fact(Skip = "FFmpeg tests are currently disabled (incomplete/unreliable).")]
    public void FFmpeg_SupportsRequiredPlaybackFeatures()
    {
        var decoders = RunFFmpegCommand("-decoders");
        decoders.Output.ShouldContain("h264");

        var demuxers = RunFFmpegCommand("-demuxers");
        demuxers.Output.ShouldContain("concat");
    }

    private static (int ExitCode, string Output) RunFFmpegCommand(string args)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(3000);

            return (process.ExitCode, output + error);
        }
        catch (Exception ex)
        {
            return (-1, $"Failed to run ffmpeg: {ex.Message}");
        }
    }
}
