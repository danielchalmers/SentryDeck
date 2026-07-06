using Serilog;

namespace SentryReplay;

/// <summary>
/// Ordered clip selection state for playback.
/// </summary>
public sealed class ClipPlaylist
{
    private readonly List<CamClip> _clips = [];
    private int _currentIndex = -1;

    public IReadOnlyList<CamClip> Clips => _clips;

    public CamClip CurrentClip => IsValidIndex(_currentIndex) ? _clips[_currentIndex] : null;

    public int CurrentIndex => _currentIndex;

    public bool HasPrevious => _currentIndex > 0;

    public bool HasNext => _currentIndex < _clips.Count - 1;

    /// <summary>
    /// The clip that <see cref="MoveNext"/> would land on, or null when there isn't one. Used to
    /// prewarm the next clip's media source while the current one is still playing.
    /// </summary>
    public CamClip NextClip => IsValidIndex(_currentIndex + 1) ? _clips[_currentIndex + 1] : null;

    public event EventHandler<CamClip> CurrentClipChanged;
    public event EventHandler PlaylistChanged;

    public void SetClips(IEnumerable<CamClip> clips)
    {
        _clips.Clear();
        _clips.AddRange(clips ?? []);
        _currentIndex = -1;

        Log.Debug("Playlist set. ClipCount={ClipCount}", _clips.Count);
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
        CurrentClipChanged?.Invoke(this, CurrentClip);
    }

    public bool MoveNext()
    {
        return MoveTo(_currentIndex + 1);
    }

    public bool MovePrevious()
    {
        return MoveTo(_currentIndex - 1);
    }

    public bool MoveTo(CamClip clip)
    {
        return clip is not null && MoveTo(_clips.IndexOf(clip));
    }

    public bool MoveTo(int index)
    {
        if (!IsValidIndex(index) || index == _currentIndex)
        {
            return false;
        }

        _currentIndex = index;
        CurrentClipChanged?.Invoke(this, CurrentClip);
        return true;
    }

    public void Clear()
    {
        SetClips([]);
    }

    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < _clips.Count;
    }
}
