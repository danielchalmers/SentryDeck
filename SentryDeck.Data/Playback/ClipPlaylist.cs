using Serilog;

namespace SentryDeck;

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

    /// <summary>
    /// Removes a clip from the playlist while keeping the current selection pointed at the same
    /// clip. Removing the current clip itself clears the selection (the caller owns stopping
    /// playback); a clip before the current one shifts the index down to compensate. Returns false
    /// when the clip isn't in the playlist.
    /// </summary>
    public bool RemoveClip(CamClip clip)
    {
        var index = clip is null ? -1 : _clips.IndexOf(clip);
        if (index < 0)
        {
            return false;
        }

        _clips.RemoveAt(index);

        if (index < _currentIndex)
        {
            _currentIndex--;
        }
        else if (index == _currentIndex)
        {
            _currentIndex = -1;
        }

        Log.Debug("Removed clip from playlist. ClipName={ClipName}; ClipCount={ClipCount}", clip.Name, _clips.Count);
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
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
