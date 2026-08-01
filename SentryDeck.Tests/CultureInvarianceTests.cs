using System.Globalization;
using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Pins the InvariantCulture arguments that the rest of the suite cannot see, because every other test runs under the dev/CI machine's own culture where the invariant and current formats happen to agree.
/// Each test here runs under a culture that formats or parses differently, so dropping one of those arguments in production turns from invisible into a failure.
/// </summary>
public sealed class CultureInvarianceTests : IDisposable
{
    // The playlist directory is shared with the running app, so only the files this class wrote get deleted.
    // Without this every run leaves a permanent, never-reused playlist behind: the file name hashes the clip's root path, and every fixture clip lives under a fresh GUID folder.
    private readonly List<string> _writtenPlaylists = [];

    public void Dispose()
    {
        foreach (var path in _writtenPlaylists)
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void BuildConcatScript_UsesInvariantDecimalSeparator(string culture)
    {
        // de-DE writes "12,500000" and ar-SA writes an Arabic decimal separator (U+066B).
        // FFmpeg's concat demuxer only understands an ASCII period, so either one makes every trimmed export fail on a machine whose only fault is its regional settings.
        using var cultureSwap = new CultureSwap(culture);

        var script = ClipExporter.BuildConcatScript([(@"C:\clips\a.mp4", TimeSpan.FromSeconds(12.5), null)]);

        script.ShouldContain("inpoint 12.500000");
    }

    [Fact]
    public void Build_WritesPlaylistWithInvariantDurations()
    {
        // Same hazard on the playback side: a de-DE "duration 60,000000" is a malformed directive and the whole clip fails to open, not just the one chunk.
        // The fixture is created before the swap so its file names stay Gregorian.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        using var cultureSwap = new CultureSwap("de-DE");

        var mediaSource = new FfconcatMediaSourceBuilder().Build(clipFiles.Clip);
        _writtenPlaylists.AddRange(mediaSource.CameraPlaylistPaths.Values);

        File.ReadAllText(mediaSource.CameraPlaylistPaths[CameraNames.Front])
            .ShouldContain("duration 60.000000");
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void FindFiles_ParsesTeslaFileNames_UnderNonGregorianCulture(string culture)
    {
        // TeslaCam names files with a Gregorian date regardless of who owns the car.
        // Parsed against the current culture instead, ar-SA (UmAlQura) rejects the name outright -- the scan finds zero clips -- and th-TH (Buddhist) dates every clip 543 years off.
        var root = Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllBytes(
                Path.Combine(root, "2023-02-23_14-14-48-front.mp4"),
                TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60)));

            using var cultureSwap = new CultureSwap(culture);

            var file = CamFile.FindFiles(root).ShouldHaveSingleItem();

            file.Timestamp.Year.ShouldBe(2023);
            file.Camera.ShouldBe(CameraNames.Front);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClipName_IsCultureInvariant()
    {
        // The clip's display name is the folder timestamp round-tripped through a parse and a format.
        // Under th-TH the current culture would corrupt both halves -- parsing 2023 as a Buddhist year and formatting it back in the Buddhist calendar.
        using var cultureSwap = new CultureSwap("th-TH");

        var clip = CamClip.Map("Mocks/2023-02-23_14-16-15");

        clip.ShouldNotBeNull();
        clip.Name.ShouldBe("02/23/2023 14:16:15");
    }

    /// <summary>
    /// Runs a test body under a chosen culture and puts the original back.
    /// CurrentCulture is per-thread and flows with the execution context, so the swap cannot leak into tests running in parallel on other threads.
    /// </summary>
    private sealed class CultureSwap : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureSwap(string name)
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
        }
    }
}
