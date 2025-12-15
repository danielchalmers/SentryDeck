using Serilog;
using TeslaCam.Data;

namespace TeslaCam;

/// <summary>
/// Manages a playlist of clips for seamless sequential playback.
/// Handles pre-rendering of upcoming clips for smooth transitions.
/// </summary>
public sealed class ClipPlaylist : IDisposable
{
    private readonly List<CamClip> _clips = [];
    private int _currentIndex = -1;
    private bool _isDisposed;

    public ClipPlaylist()
    {
    }

    public IReadOnlyList<CamClip> Clips => _clips;

    public CamClip CurrentClip => _currentIndex >= 0 && _currentIndex < _clips.Count ? _clips[_currentIndex] : null;

    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (value < -1 || value >= _clips.Count)
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
    public bool HasNext => _currentIndex < _clips.Count - 1;

    public event EventHandler<CamClip> CurrentClipChanged;
    public event EventHandler PlaylistChanged;

    public void SetClips(IEnumerable<CamClip> clips)
    {
        _clips.Clear();
        _clips.AddRange(clips);
        _currentIndex = -1; // Don't auto-select first clip

        Log.Information($"Playlist set with {_clips.Count} clips");
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _clips.Clear();
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
        var index = _clips.IndexOf(clip);
        if (index >= 0)
        {
            CurrentIndex = index;
        }
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < _clips.Count)
        {
            CurrentIndex = index;
        }
    }

    public CamClip PeekNext()
    {
        return HasNext ? _clips[_currentIndex + 1] : null;
    }

    public CamClip PeekPrevious()
    {
        return HasPrevious ? _clips[_currentIndex - 1] : null;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Clear();
    }
}
