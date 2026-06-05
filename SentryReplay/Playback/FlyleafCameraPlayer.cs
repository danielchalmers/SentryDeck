using System.ComponentModel;
using System.Windows.Media;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

namespace SentryReplay;

/// <summary>
/// Flyleaf-backed player for one camera view.
/// </summary>
internal sealed class FlyleafCameraPlayer : ICameraPlayer
{
    private readonly FlyleafHost _host;
    private readonly Player _player;
    private bool _isDisposed;
    private bool _isOpen;
    private bool _isStopping;

    public FlyleafCameraPlayer(FlyleafHost host, bool audioEnabled)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _player = new Player(CreateConfig(audioEnabled));
        _host.Player = _player;

        _player.PlaybackStopped += OnPlaybackStopped;
        _player.PropertyChanged += OnPropertyChanged;
    }

    public event EventHandler Opened;
    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public bool IsOpen => _isOpen;

    public double Speed
    {
        get => _player.Speed;
        set
        {
            if (value > 0)
            {
                _player.Speed = value;
            }
        }
    }

    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        _isStopping = false;

        try
        {
            var result = await Task.Run(() => _player.Open(
                path,
                defaultPlaylistItem: true,
                defaultVideo: true,
                defaultAudio: _player.Config.Audio.Enabled,
                defaultSubtitles: false,
                forceSubtitles: false));

            _isOpen = result.Success;
            if (result.Success)
            {
                Opened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                RaiseFailed(result.Error);
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            _isOpen = false;
            Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(ex));
            return false;
        }
    }

    public Task PlayAsync()
    {
        ThrowIfDisposed();
        _player.Play();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        ThrowIfDisposed();
        _player.Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.Run(StopAndClose);
    }

    public Task CloseAsync()
    {
        return Task.Run(StopAndClose);
    }

    public Task SeekAsync(TimeSpan position)
    {
        ThrowIfDisposed();

        if (!_isOpen)
        {
            return Task.CompletedTask;
        }

        var milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, int.MaxValue);
        _player.SeekAccurate(milliseconds);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _player.PlaybackStopped -= OnPlaybackStopped;
        _player.PropertyChanged -= OnPropertyChanged;
        _host.Player = null;
        _player.Dispose();
    }

    private static Config CreateConfig(bool audioEnabled)
    {
        return new Config
        {
            Player =
            {
                AutoPlay = false,
                SeekAccurate = true,
            },
            Video =
            {
                BackColor = Colors.Black,
            },
            Audio =
            {
                Enabled = audioEnabled,
            },
            Subtitles =
            {
                Enabled = false,
            },
            Data =
            {
                Enabled = false,
            },
        };
    }

    private void StopAndClose()
    {
        if (_isDisposed)
            return;

        try
        {
            _isStopping = true;
            _player.Stop();
        }
        finally
        {
            _isOpen = false;
        }
    }

    private void OnPlaybackStopped(object sender, PlaybackStoppedArgs e)
    {
        if (_isStopping || _isDisposed)
            return;

        if (!string.IsNullOrWhiteSpace(e.Error))
        {
            _isOpen = false;
            RaiseFailed(e.Error);
            return;
        }

        if (_player.Status == Status.Ended)
        {
            _isOpen = false;
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed || !_isOpen || e.PropertyName != nameof(Player.CurTime))
            return;

        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(TimeSpan.FromTicks(_player.CurTime)));
    }

    private void RaiseFailed(string error)
    {
        var exception = new InvalidOperationException(string.IsNullOrWhiteSpace(error)
            ? "Flyleaf failed to play the media."
            : error);
        Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(exception));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
