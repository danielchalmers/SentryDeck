using Serilog;

namespace SentryReplay;

/// <summary>
/// Manages a playlist of clips for seamless sequential playback.
/// Handles pre-rendering of upcoming clips for smooth transitions.
/// </summary>
public sealed class ClipPlaylist : IDisposable
{
    private readonly List<CamClip> ClipsInternal = [];
    private int _currentIndex = -1;
    private bool _isDisposed;

    public ClipPlaylist()
    {
    }

    public IReadOnlyList<CamClip> Clips => ClipsInternal;

    public CamClip CurrentClip => _currentIndex >= 0 && _currentIndex < ClipsInternal.Count ? ClipsInternal[_currentIndex] : null;

    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (value < -1 || value >= ClipsInternal.Count)
                return;

            var oldIndex = _currentIndex;
            _currentIndex = value;

            if (oldIndex != value)
            {
                CurrentClipChanged?.Invoke(this, CurrentClip);
            }
        }
    }

    public bool HasPrevious => _currentIndex > 0;
    public bool HasNext => _currentIndex < ClipsInternal.Count - 1;

    public event EventHandler<CamClip> CurrentClipChanged;
    public event EventHandler PlaylistChanged;

    public void SetClips(IEnumerable<CamClip> clips)
    {
        ClipsInternal.Clear();
        ClipsInternal.AddRange(clips);
        _currentIndex = -1; // Don't auto-select first clip

        Log.Debug("Playlist set. ClipCount={ClipCount}", ClipsInternal.Count);
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        ClipsInternal.Clear();
        _currentIndex = -1;
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool MoveNext()
    {
        if (!HasNext)
            return false;

        CurrentIndex++;
        return true;
    }

    public bool MovePrevious()
    {
        if (!HasPrevious)
            return false;

        CurrentIndex--;
        return true;
    }

    public void MoveTo(CamClip clip)
    {
        var index = ClipsInternal.IndexOf(clip);
        if (index >= 0)
        {
            CurrentIndex = index;
        }
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < ClipsInternal.Count)
        {
            CurrentIndex = index;
        }
    }

    public CamClip PeekNext()
    {
        return HasNext ? ClipsInternal[_currentIndex + 1] : null;
    }

    public CamClip PeekPrevious()
    {
        return HasPrevious ? ClipsInternal[_currentIndex - 1] : null;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Clear();
    }
}
