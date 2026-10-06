using System.Net.Http;
using System.Net.Sockets;

namespace SentryDeck.Tests;

public sealed partial class MainWindowViewModelTests
{
    // --- First run without FFmpeg: nothing plays until it is downloaded, so the prompt that downloads it must survive everything else the user does. ---

    private const string ConnectionRefused = "No connection could be made because the target machine actively refused it. (127.0.0.1:9)";

    // Playback has failed to start for want of FFmpeg, as on a fresh install, and the download (if the test runs it) fails the way an offline PC does.
    private static async Task<MainWindowViewModel> ViewModelWithoutFFmpegAsync(
        Func<string, IReadOnlyList<CamClip>> clipLoader = null,
        Func<Task> downloadFFmpeg = null)
    {
        var vm = new MainWindowViewModel(
            () => null!,
            clipLoader: clipLoader,
            tryStartFlyleaf: () => false,
            downloadFFmpeg: downloadFFmpeg ?? (() => Task.FromException(new HttpRequestException(ConnectionRefused, new SocketException(10061)))));
        await vm.StartPlaybackAsync();
        return vm;
    }

    private static void ShouldOfferTheFFmpegDownload(MainWindowViewModel vm)
    {
        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("FFmpeg Required");
        vm.Error.ShowFFmpegDownloadButton.ShouldBeTrue();
        vm.Error.CanDismiss.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadClips_WhileFFmpegIsMissing_KeepsTheDownloadPrompt()
    {
        // Picking a folder or rescanning is the obvious first thing to do in a footage viewer, and it used to wipe the only Download FFmpeg button until a restart.
        var vm = await ViewModelWithoutFFmpegAsync(clipLoader: _ => TestClips.Create(2));

        await vm.Library.LoadClipsAsync(["root"]);

        vm.Library.ClipCount.ShouldBe(2);
        ShouldOfferTheFFmpegDownload(vm);
        vm.HasNoClipSelected.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadClips_WithNoRootsWhileFFmpegIsMissing_StillOffersTheDownload()
    {
        var vm = await ViewModelWithoutFFmpegAsync();

        await vm.Library.LoadClipsAsync([]);

        vm.Error.Title.ShouldBe("No dashcam footage yet");
        vm.Error.ShowFFmpegDownloadButton.ShouldBeTrue();

        vm.Error.DismissCommand.Execute(null);

        ShouldOfferTheFFmpegDownload(vm);
    }

    [Fact]
    public async Task SelectingAClip_WhileFFmpegIsMissing_KeepsTheVideoHiddenAndPlayDisabled()
    {
        // With no player, the hosts would only show black video behind a Play button that does nothing.
        var vm = await ViewModelWithoutFFmpegAsync(clipLoader: _ => TestClips.Create(2));
        await vm.Library.LoadClipsAsync(["root"]);

        vm.Library.SelectedClip = vm.Library.FilteredClips[0];

        vm.ShowVideoHosts.ShouldBeFalse();
        vm.Playback.CanPlayPause.ShouldBeFalse();
        ShouldOfferTheFFmpegDownload(vm);
    }

    [Fact]
    public async Task DownloadFFmpeg_WhenTheConnectionFails_ExplainsWhatToTryAndKeepsTheRetry()
    {
        var vm = await ViewModelWithoutFFmpegAsync();

        await vm.DownloadFFmpegCommand.ExecuteAsync(null);

        vm.Error.IsVisible.ShouldBeTrue();
        vm.Error.Title.ShouldBe("Download Failed");
        vm.Error.Details.ShouldContain("proxy or firewall");
        vm.Error.Details.ShouldNotContain("127.0.0.1");
        vm.Error.ShowFFmpegDownloadButton.ShouldBeTrue();
        vm.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task DismissingADownloadFailure_ReturnsToTheFFmpegPrompt()
    {
        // Dismiss used to clear the retry along with the failure, leaving no way to download FFmpeg short of a restart.
        var vm = await ViewModelWithoutFFmpegAsync();
        await vm.DownloadFFmpegCommand.ExecuteAsync(null);

        vm.Error.DismissCommand.Execute(null);

        ShouldOfferTheFFmpegDownload(vm);
    }

    [Fact]
    public async Task DownloadFFmpeg_WhileItRuns_ShowsTheLoadingOverlayInsteadOfThePromptOrBlackVideo()
    {
        var download = new TaskCompletionSource();
        var vm = await ViewModelWithoutFFmpegAsync(clipLoader: _ => TestClips.Create(1), downloadFFmpeg: () => download.Task);
        await vm.Library.LoadClipsAsync(["root"]);
        vm.Library.SelectedClip = vm.Library.FilteredClips[0];

        var running = vm.DownloadFFmpegCommand.ExecuteAsync(null);

        vm.Error.IsVisible.ShouldBeFalse();
        vm.IsLoading.ShouldBeTrue();
        vm.ShowStatusOverlay.ShouldBeTrue();
        vm.ShowVideoHosts.ShouldBeFalse();

        download.SetException(new HttpRequestException(ConnectionRefused));
        await running;

        vm.Error.Title.ShouldBe("Download Failed");
        vm.Error.ShowFFmpegDownloadButton.ShouldBeTrue();
    }
}
