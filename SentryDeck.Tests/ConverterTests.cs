using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SentryDeck.Tests;

public sealed class ConverterTests
{
    private static readonly DateTime Moment = new(2023, 12, 16, 15, 53, 0);

    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void BoolToVisibilityConverter_MapsBooleanToVisibility(bool input, Visibility expected)
    {
        var converter = new BoolToVisibilityConverter();

        var result = converter.Convert(input, typeof(Visibility), null, null);

        result.ShouldBe(expected);
    }

    [Fact]
    public void BoolToVisibilityConverter_CollapsesNonBooleanValues()
    {
        var converter = new BoolToVisibilityConverter();

        converter.Convert(null, typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
        converter.Convert("true", typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
    }

    // The seek bar overlays position against the THUMB CENTER, which WPF's Track insets by half the thumb's width at each end: fraction f on a rail of width W maps to 10 + f × (W − 20).

    [Fact]
    public void SeekOffsetConverter_MapsFractionToThumbCenter()
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([0.5, 200d], typeof(Thickness), null, null);

        result.Left.ShouldBe(100); // 10 + 0.5 × 180
        result.Top.ShouldBe(0);
    }

    [Theory]
    [InlineData(0.0, 200, 10)]   // thumb center at the left extreme sits half a thumb in
    [InlineData(1.0, 200, 190)]  // ... and half a thumb short of the right edge
    [InlineData(1.5, 200, 190)]  // clamps above 1
    [InlineData(-0.5, 200, 10)]  // clamps below 0
    public void SeekOffsetConverter_ClampsFractionToThumbTravel(double fraction, double width, double expectedLeft)
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([fraction, width], typeof(Thickness), null, null);

        result.Left.ShouldBe(expectedLeft);
    }

    [Fact]
    public void SeekOffsetConverter_ZeroWidthYieldsZeroOffset()
    {
        var converter = new SeekOffsetConverter();

        var result = (Thickness)converter.Convert([0.5, 0d], typeof(Thickness), null, null);

        result.ShouldBe(new Thickness(0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(1.0)]
    public void SeekFillWidth_EndsExactlyAtTheThumbCenter(double fraction)
    {
        // The played fill must meet the thumb at every position, including the extremes (it used to diverge by half a thumb at 0 and 1).
        var fill = (double)new SeekFillWidthConverter().Convert([fraction, 200d], typeof(double), null, null);
        var offset = (Thickness)new SeekOffsetConverter().Convert([fraction, 200d], typeof(Thickness), null, null);

        fill.ShouldBe(offset.Left);
    }

    [Fact]
    public void SelectionWidth_SpansExactlyBetweenTheMarkOffsets()
    {
        // Band left edge (start offset) + band width must land on the end mark's offset.
        var start = (Thickness)new SeekOffsetConverter().Convert([0.25, 200d], typeof(Thickness), null, null);
        var end = (Thickness)new SeekOffsetConverter().Convert([0.75, 200d], typeof(Thickness), null, null);
        var width = (double)new SelectionWidthConverter().Convert([0.25, 0.75, 200d], typeof(double), null, null);

        (start.Left + width).ShouldBe(end.Left);
    }

    [Fact]
    public void NowPlayingConverter_MarksOnlyTheClipInstanceThatIsPlaying()
    {
        var converter = new NowPlayingConverter();
        var clips = TestClips.Create(2);

        converter.Convert([clips[0], clips[0]], typeof(Visibility), null, null).ShouldBe(Visibility.Visible);
        converter.Convert([clips[0], clips[1]], typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
    }

    [Fact]
    public void NowPlayingConverter_UnresolvedBindings_AreNotNowPlaying()
    {
        // The badge multi-binds the row's own clip against the view-model's NowPlayingClip through a proxy resource, and either leg can still be unresolved while the row's template is realized.
        // UnsetValue is a non-null singleton, so an unresolved pair used to compare equal and light up the badge on a row that is not playing.
        var converter = new NowPlayingConverter();
        var clip = TestClips.Create(1)[0];

        converter.Convert([DependencyProperty.UnsetValue, DependencyProperty.UnsetValue], typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
        converter.Convert([DependencyProperty.UnsetValue, clip], typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
        converter.Convert([clip, DependencyProperty.UnsetValue], typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
        converter.Convert([null, null], typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("Date")] // the parameter is lower-cased, so XAML casing can't silently pick the default branch
    public void FriendlyDateConverter_Parameter_SelectsOneHalfOfTheTimestamp(string parameter)
    {
        // Asserted against the unparameterized rendering instead of a literal so this holds under any current culture: the clip card lays the two halves on opposite ends of one row, and together they must be exactly what the default branch renders.
        var converter = new FriendlyDateConverter();

        var part = (string)converter.Convert(Moment, typeof(string), parameter, null);
        var combined = (string)converter.Convert(Moment, typeof(string), null, null);

        part.ShouldNotBeNullOrEmpty();
        combined.ShouldContain(part);
        combined.Length.ShouldBeGreaterThan(part.Length);
    }

    [Fact]
    public void FriendlyDateConverter_NonDateValues_RenderNothing()
    {
        var converter = new FriendlyDateConverter();

        converter.Convert(null, typeof(string), "date", null).ShouldBe(string.Empty);
        converter.Convert("2023-12-16", typeof(string), "date", null).ShouldBe(string.Empty);
    }

    [Fact]
    public void DayGroupHeaderConverter_TodayAndYesterday_GetRelativeHeaders()
    {
        // Driven off DateTime.Today rather than a fixed date so the expectation can't go stale, and with a time of day attached because the header groups by calendar day, not by instant.
        var converter = new DayGroupHeaderConverter();

        converter.Convert(DateTime.Today.AddHours(23), typeof(string), null, null).ShouldBe("Today");
        converter.Convert(DateTime.Today.AddDays(-1).AddHours(9), typeof(string), null, null).ShouldBe("Yesterday");
    }

    [Fact]
    public void DayGroupHeaderConverter_OlderDays_GetDistinctAbsoluteHeaders()
    {
        var converter = new DayGroupHeaderConverter();

        var twoDaysAgo = (string)converter.Convert(DateTime.Today.AddDays(-2), typeof(string), null, null);
        var threeDaysAgo = (string)converter.Convert(DateTime.Today.AddDays(-3), typeof(string), null, null);

        // Past yesterday each day needs a header that identifies it on its own, or two days of clips would collapse under one sticky group.
        twoDaysAgo.ShouldNotBe("Today");
        twoDaysAgo.ShouldNotBe("Yesterday");
        twoDaysAgo.ShouldNotBe(threeDaysAgo);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(1, "~1 min")]
    [InlineData(60, "~1h 0m")]
    [InlineData(95, "~1h 35m")]
    public void ClipDurationConverter_RendersTheModeledChunkDuration(int chunkCount, string expected)
    {
        // A clip built without a scan has no measured final chunk, so every chunk models 60s and the chunk count is the estimate in minutes.
        // A clip whose chunks were all filtered out has nothing to estimate.
        var result = new ClipDurationConverter().Convert(ClipWithChunks(chunkCount), typeof(string), null, null);

        result.ShouldBe(expected);
    }

    [Fact]
    public void ClipDurationConverter_ScannedClipEndingInAPartialChunk_ReadsTheRecordedLength()
    {
        // Ten full minutes plus the short tail Tesla writes when recording stops: the player shows about 10:08, so the list must not say "~11 min".
        var durations = Enumerable.Repeat(TimeSpan.FromSeconds(60), 10).Append(TimeSpan.FromSeconds(8.5)).ToList();
        using var files = TestClipFiles.Create(durations.Count, chunkDurations: durations);

        var result = new ClipDurationConverter().Convert(CamClip.Map(files.RootPath), typeof(string), null, null);

        result.ShouldBe("~10 min");
    }

    [Fact]
    public void ClipDurationConverter_ScannedClipShorterThanHalfAMinute_ReadsLessThanAMinute()
    {
        // A 10s clip used to read "~1 min"; rounding it down to "0 min" or the no-footage dash would be just as wrong.
        using var files = TestClipFiles.Create(1, chunkDurations: [TimeSpan.FromSeconds(10)]);

        var result = new ClipDurationConverter().Convert(CamClip.Map(files.RootPath), typeof(string), null, null);

        result.ShouldBe("<1 min");
    }

    [Fact]
    public void ClipDurationConverter_NonClipValues_RenderNothing()
    {
        var converter = new ClipDurationConverter();

        converter.Convert(null, typeof(string), null, null).ShouldBe(string.Empty);
        converter.Convert("5 min", typeof(string), null, null).ShouldBe(string.Empty);
    }

    [Fact]
    public void ThumbnailConverter_MissingThumbnail_YieldsNoImage()
    {
        var converter = new ThumbnailConverter();

        converter.Convert(null, typeof(ImageSource), null, null).ShouldBeNull();
        converter.Convert(string.Empty, typeof(ImageSource), null, null).ShouldBeNull();
        converter.Convert(MissingThumbnailPath(), typeof(ImageSource), null, null).ShouldBeNull();
    }

    [Fact]
    public void ThumbnailConverter_UndecodableThumbnail_YieldsNoImage()
    {
        // Tesla writes thumb.png as it records, so a half-written or truncated one is normal.
        // It has to land on the same "no thumbnail" path as a missing file instead of throwing out of a binding while the list scrolls.
        using var thumbnail = new TempFile("not a png"u8.ToArray(), ".png");

        new ThumbnailConverter().Convert(thumbnail.Path, typeof(ImageSource), null, null).ShouldBeNull();
    }

    [Fact]
    public void ThumbnailConverter_ValidThumbnail_YieldsADecodedImageAndReleasesTheFile()
    {
        using var thumbnail = new TempFile(BuildPng(width: 384, height: 288), ".png");

        var image = new ThumbnailConverter().Convert(thumbnail.Path, typeof(ImageSource), null, null).ShouldBeOfType<BitmapImage>();

        // Decoded straight to list size, and fully read up front: the file must not stay locked, or deleting the clip's folder fails.
        image.PixelWidth.ShouldBe(192);
        image.IsFrozen.ShouldBeTrue();
        using var exclusive = new FileStream(thumbnail.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void ThumbnailConverter_FallbackParameter_IsVisibleOnlyWhenTheFileIsMissing()
    {
        // The placeholder behind the image is driven purely by the file's presence.
        var converter = new ThumbnailConverter();

        converter.Convert(MissingThumbnailPath(), typeof(Visibility), "fallback", null).ShouldBe(Visibility.Visible);
        converter.Convert(null, typeof(Visibility), "fallback", null).ShouldBe(Visibility.Visible);

        using var thumbnail = new TempFile("not a png"u8.ToArray(), ".png");
        converter.Convert(thumbnail.Path, typeof(Visibility), "fallback", null).ShouldBe(Visibility.Collapsed);
    }

    [Fact]
    public void EventConverters_NonEventValues_FallBackToTheNoEventDefaults()
    {
        // These bind against clip rows that may carry no event.json at all.
        new ReasonLabelConverter().Convert(null, typeof(string), null, null).ShouldBe("Recent");
        new ReasonKeyConverter().Convert(null, typeof(string), null, null).ShouldBe(ClipDisplay.ReasonRecent);
        new MapAvailabilityConverter().Convert("not an event", typeof(Visibility), null, null).ShouldBe(Visibility.Collapsed);
    }

    private static CamClip ClipWithChunks(int chunkCount)
    {
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(index => new CamChunk(Moment.AddMinutes(index), []));

        return new CamClip(Path.GetTempPath(), "Test Clip", Moment, chunks, camEvent: null);
    }

    private static byte[] BuildPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string MissingThumbnailPath() =>
        Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}.png");
}
