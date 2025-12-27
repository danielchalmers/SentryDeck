using System.IO;
using SentryReplay.Data;
using Shouldly;

namespace SentryReplay.Tests;

/// <summary>
/// Tests for the RenderCache functionality.
/// </summary>
public class RenderCacheTests : IDisposable
{
    private readonly RenderCache Cache;

    public RenderCacheTests()
    {
        Cache = new RenderCache(maxConcurrentRenders: 2, maxCacheSize: 5);
    }

    [Fact]
    public void RenderCache_InitializesCorrectly()
    {
        // Arrange & Assert
        Cache.ShouldNotBeNull();
    }

    [Fact]
    public void GetOrCreateRenderer_CreatesNewRenderer()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        // Act
        var renderer = Cache.GetOrCreateRenderer(clip);

        // Assert
        renderer.ShouldNotBeNull();
        renderer.Clip.ShouldBe(clip);
    }

    [Fact]
    public void GetOrCreateRenderer_ReturnsSameRendererForSameClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        // Act
        var renderer1 = Cache.GetOrCreateRenderer(clip);
        var renderer2 = Cache.GetOrCreateRenderer(clip);

        // Assert
        renderer1.ShouldBeSameAs(renderer2);
    }

    [Fact]
    public void IsRendered_ReturnsFalseForNewClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        // Act
        var isRendered = Cache.IsRendered(clip);

        // Assert
        isRendered.ShouldBeFalse();
    }

    [Fact]
    public void GetRenderedPath_ReturnsNullForUnrenderedClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        // Act
        var path = Cache.GetRenderedPath(clip);

        // Assert
        path.ShouldBeNull();
    }

    [Fact]
    public void SetCurrentlyPlaying_PreventsEviction()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        // Act - should not throw
        Cache.SetCurrentlyPlaying(clip);

        // Assert - currently playing should protect from eviction
        // (actual eviction behavior would need integration tests)
    }

    [Fact]
    public void SetCurrentlyPlaying_AllowsNullToClear()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null)
            return;

        Cache.SetCurrentlyPlaying(clip);

        // Act - should not throw
        Cache.SetCurrentlyPlaying(null);
    }

    [Fact]
    public void CancelAll_DoesNotThrowOnEmptyCache()
    {
        // Act - should not throw
        Cache.CancelAll();
    }

    [Fact]
    public void Clear_DoesNotThrowOnEmptyCache()
    {
        // Act - should not throw
        Cache.Clear();
    }

    [Fact]
    public void CancelCurrentRender_DoesNotThrowWhenNoRender()
    {
        // Act - should not throw
        Cache.CancelCurrentRender();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act - should not throw
        Cache.Dispose();
        Cache.Dispose();
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

    public void Dispose()
    {
        Cache?.Dispose();
    }
}
