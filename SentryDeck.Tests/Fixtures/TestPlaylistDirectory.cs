using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// A private playlist directory for a test that drives the real <see cref="FfconcatMediaSourceBuilder"/>.
/// The builder names each playlist after a hash of the clip folder, and fixture clips live under a fresh GUID root every run, so tests sharing the app's %TEMP% directory would leave a permanent, never-reused file behind for every clip they ever built.
/// </summary>
internal sealed class TestPlaylistDirectory : IDisposable
{
    public TestPlaylistDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SentryDeckPlaylists-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public FfconcatMediaSourceBuilder CreateBuilder() => new() { PlaylistDirectory = Path };

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
