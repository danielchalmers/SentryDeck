using System.IO;
using Serilog;

namespace SentryReplay;

public sealed class OverlayCameraLoader : IDisposable
{
    private readonly IReadOnlyList<KeyValuePair<string, IMediaPlayer>> OverlayPlayers;
    private readonly Func<CamChunk> GetCurrentChunk;
    private readonly Func<long, bool> IsRequestActive;
    private readonly Func<double> GetPlaybackSpeed;
    private readonly Func<bool> GetIsPlaying;
    private CancellationTokenSource _overlayCts;

    public OverlayCameraLoader(
        IEnumerable<KeyValuePair<string, IMediaPlayer>> overlayPlayers,
        Func<CamChunk> getCurrentChunk,
        Func<long, bool> isRequestActive,
        Func<double> getPlaybackSpeed,
        Func<bool> getIsPlaying)
    {
        OverlayPlayers = overlayPlayers?.ToList() ?? throw new ArgumentNullException(nameof(overlayPlayers));
        GetCurrentChunk = getCurrentChunk ?? throw new ArgumentNullException(nameof(getCurrentChunk));
        IsRequestActive = isRequestActive ?? throw new ArgumentNullException(nameof(isRequestActive));
        GetPlaybackSpeed = getPlaybackSpeed ?? throw new ArgumentNullException(nameof(getPlaybackSpeed));
        GetIsPlaying = getIsPlaying ?? throw new ArgumentNullException(nameof(getIsPlaying));
    }

    public void Start(CamChunk chunk, TimeSpan offset, long requestId, CancellationToken cancellationToken)
    {
        Cancel();

        var overlayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _overlayCts = overlayCts;
        _ = LoadOverlayPlayersAsync(chunk, offset, requestId, overlayCts);
    }

    public void Cancel()
    {
        var cts = _overlayCts;
        if (cts is null)
            return;

        _overlayCts = null;
        cts.Cancel();
    }

    public void Dispose()
    {
        Cancel();
    }

    private async Task LoadOverlayPlayersAsync(CamChunk chunk, TimeSpan offset, long requestId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string cameraName = null;
        string filePath = null;

        try
        {
            await Task.Yield();

            foreach (var (camera, player) in OverlayPlayers)
            {
                cameraName = camera;
                filePath = null;
                ct.ThrowIfCancellationRequested();

                if (!IsRequestActive(requestId) || chunk != GetCurrentChunk())
                    return;

                if (!chunk.Files.TryGetValue(camera, out var file) || !File.Exists(file.FullPath))
                {
                    Log.Debug(
                        "Overlay camera file not available. Camera={Camera}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}",
                        camera,
                        chunk.Timestamp,
                        requestId);
                    continue;
                }

                filePath = file.FullPath;
                if (player.IsOpen)
                {
                    player.SpeedRatio = GetPlaybackSpeed();
                    continue;
                }

                await player.CloseAsync();
                var opened = await player.OpenAsync(new Uri(file.FullPath));
                if (!opened)
                {
                    Log.Warning(
                        "Failed to open overlay camera. Camera={Camera}; File={File}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}",
                        camera,
                        file.FullPath,
                        chunk.Timestamp,
                        requestId);
                    continue;
                }

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != GetCurrentChunk())
                {
                    await player.CloseAsync();
                    return;
                }

                player.SpeedRatio = GetPlaybackSpeed();

                if (offset > TimeSpan.Zero)
                {
                    await player.SeekAsync(offset);
                }

                if (ct.IsCancellationRequested || !IsRequestActive(requestId) || chunk != GetCurrentChunk())
                {
                    await player.CloseAsync();
                    return;
                }

                if (GetIsPlaying())
                {
                    await player.PlayAsync();
                }
                else
                {
                    await player.PauseAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Failed to load overlay camera. Camera={Camera}; File={File}; ChunkTimestamp={ChunkTimestamp}; RequestId={RequestId}",
                cameraName,
                filePath,
                chunk.Timestamp,
                requestId);
        }
        finally
        {
            if (ReferenceEquals(_overlayCts, cts))
            {
                _overlayCts = null;
            }

            cts.Dispose();
        }
    }
}
