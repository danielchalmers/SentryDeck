using System.IO;
using Shouldly;

namespace TeslaCam.Tests;

/// <summary>
/// Fast unit tests for RealtimeVideoStreamer and GpuCapabilities.
/// </summary>
public class RealtimeVideoStreamerTests
{
    [Fact]
    public void GpuCapabilities_BestDecoder_ReturnsCorrectPriority()
    {
        new GpuCapabilities { HasD3D11VA = true, HasCuda = true }.BestDecoder.ShouldBe("d3d11va");
        new GpuCapabilities { HasCuda = true, HasDXVA2 = true }.BestDecoder.ShouldBe("cuda");
        new GpuCapabilities().BestDecoder.ShouldBe("auto");
    }

    [Fact]
    public void GpuCapabilities_BestEncoder_ReturnsCorrectPriority()
    {
        new GpuCapabilities { HasNvenc = true, HasAmf = true }.BestEncoder.ShouldBe("h264_nvenc");
        new GpuCapabilities { HasAmf = true, HasQsvEnc = true }.BestEncoder.ShouldBe("h264_amf");
        new GpuCapabilities().BestEncoder.ShouldBe("libx264");
    }

    [Fact]
    public void GpuCapabilities_HasAnyGpuDecoding_CorrectlyDetects()
    {
        new GpuCapabilities { HasCuda = true }.HasAnyGpuDecoding.ShouldBeTrue();
        new GpuCapabilities { HasD3D11VA = true }.HasAnyGpuDecoding.ShouldBeTrue();
        new GpuCapabilities().HasAnyGpuDecoding.ShouldBeFalse();
    }

    [Fact]
    public void GpuCapabilities_HasAnyGpuEncoding_CorrectlyDetects()
    {
        new GpuCapabilities { HasNvenc = true }.HasAnyGpuEncoding.ShouldBeTrue();
        new GpuCapabilities { HasAmf = true }.HasAnyGpuEncoding.ShouldBeTrue();
        new GpuCapabilities().HasAnyGpuEncoding.ShouldBeFalse();
    }

    [Fact]
    public void RealtimeVideoStreamer_InitializesCorrectly()
    {
        using var streamer = new RealtimeVideoStreamer();

        streamer.CurrentClip.ShouldBeNull();
        streamer.IsStreaming.ShouldBeFalse();
        streamer.Duration.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task RealtimeVideoStreamer_CanStopWithoutStarting()
    {
        using var streamer = new RealtimeVideoStreamer();

        await streamer.StopStreamAsync();

        streamer.IsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void RealtimeVideoStreamer_Dispose_CanBeCalledMultipleTimes()
    {
        var streamer = new RealtimeVideoStreamer();
        streamer.Dispose();
        streamer.Dispose(); // Should not throw
    }
}
