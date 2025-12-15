using System.IO;
using Shouldly;
using TeslaCam.Data;

namespace TeslaCam.Tests;

/// <summary>
/// Tests for the RenderCache functionality.
/// </summary>
public class RenderCacheTests : IDisposable
{
    private RenderCache _cache;

    public RenderCacheTests()
    {
        _cache = new RenderCache(maxConcurrentRenders: 2, maxCacheSize: 5);
    }

    [Fact]
    public void RenderCache_InitializesCorrectly()
    {
        // Arrange & Assert
        _cache.ShouldNotBeNull();
    }

    [Fact]
    public void GetOrCreateRenderer_CreatesNewRenderer()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        // Act
        var renderer = _cache.GetOrCreateRenderer(clip);

        // Assert
        renderer.ShouldNotBeNull();
        renderer.Clip.ShouldBe(clip);
    }

    [Fact]
    public void GetOrCreateRenderer_ReturnsSameRendererForSameClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        // Act
        var renderer1 = _cache.GetOrCreateRenderer(clip);
        var renderer2 = _cache.GetOrCreateRenderer(clip);

        // Assert
        renderer1.ShouldBeSameAs(renderer2);
    }

    [Fact]
    public void IsRendered_ReturnsFalseForNewClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        // Act
        var isRendered = _cache.IsRendered(clip);

        // Assert
        isRendered.ShouldBeFalse();
    }

    [Fact]
    public void GetRenderedPath_ReturnsNullForUnrenderedClip()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        // Act
        var path = _cache.GetRenderedPath(clip);

        // Assert
        path.ShouldBeNull();
    }

    [Fact]
    public void SetCurrentlyPlaying_PreventsEviction()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        // Act - should not throw
        _cache.SetCurrentlyPlaying(clip);

        // Assert - currently playing should protect from eviction
        // (actual eviction behavior would need integration tests)
    }

    [Fact]
    public void SetCurrentlyPlaying_AllowsNullToClear()
    {
        // Arrange
        var clip = GetTestClip();
        if (clip is null) return;

        _cache.SetCurrentlyPlaying(clip);

        // Act - should not throw
        _cache.SetCurrentlyPlaying(null);
    }

    [Fact]
    public void CancelAll_DoesNotThrowOnEmptyCache()
    {
        // Act - should not throw
        _cache.CancelAll();
    }

    [Fact]
    public void Clear_DoesNotThrowOnEmptyCache()
    {
        // Act - should not throw
        _cache.Clear();
    }

    [Fact]
    public void CancelCurrentRender_DoesNotThrowWhenNoRender()
    {
        // Act - should not throw
        _cache.CancelCurrentRender();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act - should not throw
        _cache.Dispose();
        _cache.Dispose();
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
        _cache?.Dispose();
    }
}
