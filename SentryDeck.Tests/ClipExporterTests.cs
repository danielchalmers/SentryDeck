namespace SentryDeck.Tests;

public sealed class ClipExporterTests
{
    private static readonly DateTime FirstTimestamp = new(2025, 1, 1, 12, 0, 0);

    // A clip of three 60s chunks plus the matching opened media source. Files never touch disk:
    // ResolveEntries/BuildConcatScript work purely on the model. omitCameraFromChunk removes one
    // camera's file from one chunk to exercise the truncation rules.
    private static (CamClip Clip, ClipMediaSource Source) CreateClip(
        string omitCamera = null,
        int omitFromChunkIndex = -1)
    {
        var chunks = new List<CamChunk>();

        for (var i = 0; i < 3; i++)
        {
            var timestamp = FirstTimestamp.AddMinutes(i);
            var files = CameraNames.All
                .Where(camera => !(camera == omitCamera && i == omitFromChunkIndex))
                .Select(camera => new CamFile($@"C:\clips\{timestamp:yyyy-MM-dd_HH-mm-ss}-{camera}.mp4", timestamp, camera));
            chunks.Add(new CamChunk(timestamp, files));
        }

        var clip = new CamClip(@"C:\clips", "Test Clip", FirstTimestamp, chunks, camEvent: null);

        var source = new ClipMediaSource(
            TimeSpan.FromSeconds(180),
            [TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)],
            new Dictionary<string, string>(),
            [],
            chunks.Select(c => c.Timestamp).ToList(),
            Enumerable.Repeat(TimeSpan.FromSeconds(60), 3).ToList());

        return (clip, source);
    }

    private static ClipExportRequest Request(
        (CamClip Clip, ClipMediaSource Source) fixture,
        string camera,
        TimeSpan start,
        TimeSpan end)
        => new(fixture.Clip, fixture.Source, camera, start, end, @"C:\out\export.mp4");

    [Fact]
    public void ResolveEntries_MapsSegmentsToTheCameraFiles()
    {
        var fixture = CreateClip();

        var entries = ClipExporter.ResolveEntries(
            Request(fixture, CameraNames.Front, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)));

        entries.Count.ShouldBe(2);
        entries[0].FilePath.ShouldEndWith("12-00-00-front.mp4");
        entries[0].InPoint.ShouldBe(TimeSpan.FromSeconds(30));
        entries[0].OutPoint.ShouldBeNull();
        entries[1].FilePath.ShouldEndWith("12-01-00-front.mp4");
        entries[1].InPoint.ShouldBeNull();
        entries[1].OutPoint.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ResolveEntries_TruncatesAtTheFirstMissingCameraFile()
    {
        // The back camera is missing its middle chunk: an export spanning all three keeps only
        // the first, mirroring how playback truncates that camera's playlist.
        var fixture = CreateClip(omitCamera: CameraNames.Back, omitFromChunkIndex: 1);

        var entries = ClipExporter.ResolveEntries(
            Request(fixture, CameraNames.Back, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)),
            isSideFileReadable: _ => true);

        var entry = entries.ShouldHaveSingleItem();
        entry.FilePath.ShouldEndWith("12-00-00-back.mp4");
    }

    [Fact]
    public void ResolveEntries_TruncatesAtTheFirstUnreadableSideCameraFile()
    {
        // The back camera's middle chunk is present but corrupt (no readable duration): playback
        // truncates that camera's playlist there, so the export must stop there too instead of
        // feeding the unreadable file to FFmpeg's concat demuxer.
        var fixture = CreateClip();

        var entries = ClipExporter.ResolveEntries(
            Request(fixture, CameraNames.Back, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)),
            isSideFileReadable: path => !path.EndsWith("12-01-00-back.mp4"));

        var entry = entries.ShouldHaveSingleItem();
        entry.FilePath.ShouldEndWith("12-00-00-back.mp4");
    }

    [Fact]
    public void ResolveEntries_DoesNotProbeTheFrontCamera()
    {
        // Front chunks were probe-verified when the media source was built, so the export must not
        // second-guess them (a probe failing everything would otherwise truncate the cut to nothing).
        var fixture = CreateClip();

        var entries = ClipExporter.ResolveEntries(
            Request(fixture, CameraNames.Front, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(150)),
            isSideFileReadable: _ => false);

        entries.Count.ShouldBe(3);
    }

    [Fact]
    public void ResolveEntries_NoFootageForCameraInRange_Throws()
    {
        var fixture = CreateClip(omitCamera: CameraNames.LeftRepeater, omitFromChunkIndex: 0);

        Should.Throw<InvalidOperationException>(() => ClipExporter.ResolveEntries(
                Request(fixture, CameraNames.LeftRepeater, TimeSpan.Zero, TimeSpan.FromSeconds(90)),
                isSideFileReadable: _ => true))
            .Message.ShouldContain("left repeater");
    }

    [Fact]
    public void ResolveEntries_EmptyRange_Throws()
    {
        var fixture = CreateClip();

        Should.Throw<InvalidOperationException>(() => ClipExporter.ResolveEntries(
            Request(fixture, CameraNames.Front, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300))));
    }

    [Fact]
    public void BuildConcatScript_EmitsDirectivesOnlyForSetPoints()
    {
        var script = ClipExporter.BuildConcatScript(
        [
            (@"C:\clips\a.mp4", TimeSpan.FromSeconds(12.5), null),
            (@"C:\clips\b.mp4", null, null),
            (@"C:\clips\c.mp4", null, TimeSpan.FromSeconds(3.25)),
        ]);

        var lines = script.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldBe(
        [
            "ffconcat version 1.0",
            "file 'C:/clips/a.mp4'",
            "inpoint 12.500000",
            "file 'C:/clips/b.mp4'",
            "file 'C:/clips/c.mp4'",
            "outpoint 3.250000",
        ]);
    }

    [Fact]
    public void BuildArguments_StreamCopiesTheConcatScript()
    {
        var arguments = ClipExporter.BuildArguments(@"C:\tmp\job.ffconcat", @"C:\out\clip.mp4");

        arguments.ShouldContain("-f concat");
        arguments.ShouldContain("-c copy");
        arguments.ShouldContain("\"C:\\tmp\\job.ffconcat\"");
        arguments.ShouldContain("\"C:\\out\\clip.mp4\"");
    }
}
