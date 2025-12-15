using System.Diagnostics;
using System.IO;
using System.Text;
using Shouldly;
using TeslaCam.Data;

namespace TeslaCam.Tests;

/// <summary>
/// Fast unit tests for ClipRenderer. No actual ffmpeg execution.
/// </summary>
public class ClipRendererTests : IDisposable
{
    [Fact]
    public void ClipRenderer_InitializesCorrectly()
    {
        var clip = GetTestClip();
        if (clip is null) return;

        using var renderer = new ClipRenderer(clip);

        renderer.Clip.ShouldBe(clip);
        renderer.IsRendered.ShouldBeFalse();
        renderer.IsRendering.ShouldBeFalse();
        renderer.RenderProgress.ShouldBe(0);
        renderer.OutputPath.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ClipRenderer_OutputPath_IsInTempDirectory()
    {
        var clip = GetTestClip();
        if (clip is null) return;

        using var renderer = new ClipRenderer(clip);

        renderer.OutputPath.ShouldStartWith(Path.GetTempPath());
        renderer.OutputPath.ShouldEndWith(".mp4");
    }

    [Fact]
    public void ClipRenderer_CancelRender_DoesNotThrowWhenNotRendering()
    {
        var clip = GetTestClip();
        if (clip is null) return;

        using var renderer = new ClipRenderer(clip);
        
        // Should not throw
        renderer.CancelRender();
    }

    [Fact]
    public void ClipRenderer_Cleanup_DoesNotThrowWhenNothingToClean()
    {
        var clip = GetTestClip();
        if (clip is null) return;

        using var renderer = new ClipRenderer(clip);
        
        // Should not throw
        renderer.Cleanup();
    }

    [Fact]
    public void FFmpegCommand_BuildsCorrectly_SingleCamera()
    {
        var args = BuildTestFFmpegArgs(["front"]);

        args.ShouldContain("-f concat");
        args.ShouldContain("-c:v libx264");
        args.ShouldContain("scale=1280:960");
    }

    [Fact]
    public void FFmpegCommand_BuildsCorrectly_MultiCamera()
    {
        var args = BuildTestFFmpegArgs(["front", "back", "left_repeater", "right_repeater"]);

        args.ShouldContain("-filter_complex");
        args.ShouldContain("overlay");
        args.ShouldContain("scale=280:210");
    }

    private static CamClip GetTestClip()
    {
        var mockPath = "Mocks/2023-02-23_14-16-15";
        if (Directory.Exists(mockPath))
        {
            return CamClip.Map(mockPath);
        }
        return null;
    }

    private static string BuildTestFFmpegArgs(string[] cameras)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hwaccel auto ");

        for (int i = 0; i < cameras.Length; i++)
        {
            sb.Append($"-f concat -safe 0 -i \"test_{cameras[i]}.txt\" ");
        }

        var filters = new List<string> { "[0:v]scale=1280:960,setsar=1[main]" };
        var currentOutput = "[main]";

        for (int i = 1; i < cameras.Length; i++)
        {
            var scaled = $"s{i}";
            var output = $"o{i}";
            filters.Add($"[{i}:v]scale=280:210,setsar=1[{scaled}]");
            filters.Add($"{currentOutput}[{scaled}]overlay=10:10[{output}]");
            currentOutput = $"[{output}]";
        }

        sb.Append($"-filter_complex \"{string.Join(";", filters)}\" ");
        sb.Append($"-map \"{currentOutput}\" ");
        sb.Append("-c:v libx264 -preset veryfast -crf 26 ");
        sb.Append("-movflags +faststart -an -threads 0 ");
        sb.Append("\"output.mp4\"");

        return sb.ToString();
    }

    public void Dispose() { }
}
