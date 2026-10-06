using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Tests that count the whole process's open handles run on their own.
/// Tests running in parallel open and close enough handles to hide the hundreds a regression would hold.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessHandleCountCollection
{
    public const string Name = "Process handle count";
}

[Collection(ProcessHandleCountCollection.Name)]
public sealed class CamDiscoveryHandleTests
{
    [Fact]
    public void FindClips_FolderWithManyClips_DoesNotHoldAHandleOpenPerSubfolder()
    {
        // A SavedClips or SentryClips folder can hold thousands of events.
        // The scan used to open a handle to every one of them before reading any, so its open handles grew with the library instead of staying flat.
        const int ClipCount = 500;
        using var temp = new TempDirectory();

        var savedClips = temp.CreateSubdirectory("SavedClips");
        for (var i = 0; i < ClipCount; i++)
        {
            var name = new DateTime(2023, 1, 1).AddMinutes(i).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var clip = Directory.CreateDirectory(Path.Combine(savedClips, name)).FullName;
            File.WriteAllBytes(Path.Combine(clip, $"{name}-front.mp4"), []);
        }

        // Earlier tests can leave handles waiting on finalizers, and closing them mid-scan would offset the count.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var baseline = OpenHandleCount();
        var peak = baseline;
        var found = 0;
        foreach (var _ in CamClip.FindClips(temp.Path))
        {
            if (++found % 50 == 0)
            {
                peak = Math.Max(peak, OpenHandleCount());
            }
        }

        found.ShouldBe(ClipCount);

        // One handle per clip folder reaches the full clip count; the bound leaves room for handles the runtime opens in the background.
        (peak - baseline).ShouldBeLessThan(ClipCount / 5);
    }

    private static int OpenHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }
}
