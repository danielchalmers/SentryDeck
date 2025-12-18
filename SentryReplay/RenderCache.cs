using System.Collections.Concurrent;
using System.IO;
using Serilog;
using SentryReplay.Data;

namespace SentryReplay;

/// <summary>
/// Manages rendering and caching of clip videos with background pre-rendering
/// for seamless playback transitions.
/// </summary>
public sealed class RenderCache : IDisposable
{
    private readonly ConcurrentDictionary<CamClip, ClipRenderer> _renderers = new();
    private readonly ConcurrentDictionary<CamClip, Task<bool>> _renderTasks = new();
    private readonly SemaphoreSlim _renderSemaphore;
    private readonly int _maxConcurrentRenders;
    private readonly int _maxCacheSize;
    private CamClip _currentlyPlaying;
    private CamClip _currentlyRendering;
    private bool _isDisposed;

    public RenderCache(int maxConcurrentRenders = 1, int maxCacheSize = 10)
    {
        _maxConcurrentRenders = maxConcurrentRenders;
        _maxCacheSize = maxCacheSize;
        _renderSemaphore = new SemaphoreSlim(maxConcurrentRenders);
    }

    public event EventHandler<(CamClip Clip, double Progress)> RenderProgress;
    public event EventHandler<CamClip> RenderCompleted;
    public event EventHandler<(CamClip Clip, string Error)> RenderFailed;

    /// <summary>
    /// Marks a clip as currently playing so it won't be evicted from cache.
    /// </summary>
    public void SetCurrentlyPlaying(CamClip clip)
    {
        _currentlyPlaying = clip;
    }

    /// <summary>
    /// Cancels the render currently in progress, if any.
    /// </summary>
    public void CancelCurrentRender()
    {
        if (_currentlyRendering is not null && _renderers.TryGetValue(_currentlyRendering, out var renderer))
        {
            Log.Debug($"Cancelling render for: {_currentlyRendering.Name}");
            renderer.CancelRender();
        }
    }

    /// <summary>
    /// Gets a renderer for the specified clip, creating one if necessary.
    /// </summary>
    public ClipRenderer GetOrCreateRenderer(CamClip clip)
    {
        return _renderers.GetOrAdd(clip, c => new ClipRenderer(c));
    }

    /// <summary>
    /// Checks if a clip is already rendered and ready for playback.
    /// </summary>
    public bool IsRendered(CamClip clip)
    {
        return _renderers.TryGetValue(clip, out var renderer) && renderer.IsRendered;
    }

    /// <summary>
    /// Gets the output path for a rendered clip, or null if not rendered.
    /// </summary>
    public string GetRenderedPath(CamClip clip)
    {
        if (_renderers.TryGetValue(clip, out var renderer) && renderer.IsRendered)
        {
            return renderer.OutputPath;
        }
        return null;
    }

    /// <summary>
    /// Renders a clip asynchronously, returning the output path when complete.
    /// </summary>
    public async Task<string> RenderAsync(CamClip clip, CancellationToken cancellationToken = default)
    {
        var renderer = GetOrCreateRenderer(clip);

        // If already rendered, return immediately
        if (renderer.IsRendered)
        {
            return renderer.OutputPath;
        }

        // Check if render is already in progress
        var existingTask = _renderTasks.GetOrAdd(clip, _ => StartRenderInternal(clip, renderer, cancellationToken));

        try
        {
            var success = await existingTask;
            return success ? renderer.OutputPath : null;
        }
        finally
        {
            _renderTasks.TryRemove(clip, out _);
        }
    }

    private async Task<bool> StartRenderInternal(CamClip clip, ClipRenderer renderer, CancellationToken cancellationToken)
    {
        await _renderSemaphore.WaitAsync(cancellationToken);
        _currentlyRendering = clip;

        try
        {
            // Wire up progress events
            renderer.ProgressChanged += (_, progress) => RenderProgress?.Invoke(this, (clip, progress));

            var success = await renderer.RenderAsync(cancellationToken);

            if (success)
            {
                RenderCompleted?.Invoke(this, clip);
                EnforceMaxCacheSize();
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                RenderFailed?.Invoke(this, (clip, "Render failed"));
            }

            return success;
        }
        finally
        {
            _currentlyRendering = null;
            _renderSemaphore.Release();
        }
    }

    /// <summary>
    /// Queues clips for background pre-rendering (e.g., next clips in playlist).
    /// </summary>
    public void QueuePrerender(IEnumerable<CamClip> clips)
    {
        foreach (var clip in clips)
        {
            if (!IsRendered(clip) && !_renderTasks.ContainsKey(clip))
            {
                var renderer = GetOrCreateRenderer(clip);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _renderSemaphore.WaitAsync();
                        try
                        {
                            if (!renderer.IsRendered)
                            {
                                await renderer.RenderAsync();
                                RenderCompleted?.Invoke(this, clip);
                            }
                        }
                        finally
                        {
                            _renderSemaphore.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, $"Pre-render failed for {clip.Name}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Cancels all pending renders.
    /// </summary>
    public void CancelAll()
    {
        foreach (var renderer in _renderers.Values)
        {
            renderer.CancelRender();
        }
        _renderTasks.Clear();
    }

    /// <summary>
    /// Removes old cached renders to stay under the max cache size.
    /// Won't evict the currently playing clip.
    /// </summary>
    private void EnforceMaxCacheSize()
    {
        var rendered = _renderers.Values
            .Where(r => r.IsRendered && r.Clip != _currentlyPlaying)
            .OrderBy(r => File.GetLastAccessTime(r.OutputPath))
            .ToList();

        while (rendered.Count > _maxCacheSize - 1) // Keep one slot for current
        {
            var oldest = rendered[0];
            rendered.RemoveAt(0);

            if (_renderers.TryRemove(oldest.Clip, out var removed))
            {
                removed.Cleanup();
                Log.Debug($"Evicted from cache: {oldest.Clip.Name}");
            }
        }
    }

    /// <summary>
    /// Clears all cached renders.
    /// </summary>
    public void Clear()
    {
        CancelAll();

        foreach (var renderer in _renderers.Values)
        {
            renderer.Dispose();
        }

        _renderers.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Clear();
        _renderSemaphore.Dispose();
    }
}
