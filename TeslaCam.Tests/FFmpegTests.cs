using System.Diagnostics;
using Shouldly;

namespace TeslaCam.Tests;

/// <summary>
/// Fast tests for FFmpeg availability and capabilities.
/// </summary>
public class FFmpegTests
{
    [Fact]
    public void FFmpeg_IsAvailable()
    {
        var result = RunFFmpegCommand("-version");
        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("ffmpeg version");
    }

    [Fact]
    public void FFmpeg_SupportsRequiredFeatures()
    {
        var encoders = RunFFmpegCommand("-encoders");
        encoders.Output.ShouldContain("libx264");

        var demuxers = RunFFmpegCommand("-demuxers");
        demuxers.Output.ShouldContain("concat");

        var filters = RunFFmpegCommand("-filters");
        filters.Output.ShouldContain("overlay");
        filters.Output.ShouldContain("scale");
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
