using System.Net;
using System.Net.Http;

namespace SentryDeck.Tests;

public sealed class AboutViewModelTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_BeforeGitHubAnswers_SaysChecking()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>();
        var about = new AboutViewModel(new UpdateService(new HttpClient(new StubHandler(_ => reply.Task))));

        var check = about.CheckForUpdatesAsync();

        about.UpdateCheckState.ShouldBe(UpdateCheckState.Checking);
        about.UpdateStatusTitle.ShouldBe("Checking for updates…");
        about.UpdateStatusDetails.ShouldBe("Looking for a newer release on GitHub.");

        reply.SetResult(Releases("v999.0.0"));
        await check;

        about.UpdateStatusTitle.ShouldBe("Update available");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)] // GitHub's unauthenticated rate limit
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckForUpdatesAsync_WhenGitHubRefuses_SaysTheCheckFailed(HttpStatusCode statusCode)
    {
        var about = new AboutViewModel(new UpdateService(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(statusCode))))));

        await about.CheckForUpdatesAsync();

        about.UpdateCheckState.ShouldBe(UpdateCheckState.Failed);
        about.HasUpdateBadge.ShouldBeFalse();
        about.UpdateStatusTitle.ShouldBe("Couldn't check for updates");
        about.UpdateStatusDetails.ShouldBe("The latest release couldn't be read from GitHub.");
        about.LatestReleaseUrl.ShouldBe(UpdateService.ReleasesPageUrl);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenOffline_SaysTheCheckFailed()
    {
        var about = new AboutViewModel(new UpdateService(new HttpClient(new StubHandler(_ => throw new HttpRequestException("No connection could be made.")))));

        await about.CheckForUpdatesAsync();

        about.UpdateCheckState.ShouldBe(UpdateCheckState.Failed);
        about.UpdateStatusTitle.ShouldBe("Couldn't check for updates");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenNoNewerReleaseIsOut_SaysUpToDate()
    {
        var about = new AboutViewModel(new UpdateService(new HttpClient(new StubHandler(_ => Task.FromResult(Releases("v0.0.0"))))));

        await about.CheckForUpdatesAsync();

        about.UpdateCheckState.ShouldBe(UpdateCheckState.Checked);
        about.HasUpdateBadge.ShouldBeFalse();
        about.UpdateStatusTitle.ShouldBe("You're up to date");
        about.UpdateStatusDetails.ShouldBe("No newer release was found.");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenANewerReleaseIsOut_SaysUpdateAvailable()
    {
        var about = new AboutViewModel(new UpdateService(new HttpClient(new StubHandler(_ => Task.FromResult(Releases("v999.0.0"))))));

        await about.CheckForUpdatesAsync();

        about.UpdateCheckState.ShouldBe(UpdateCheckState.Checked);
        about.HasUpdateBadge.ShouldBeTrue();
        about.UpdateStatusTitle.ShouldBe("Update available");
        about.UpdateStatusDetails.ShouldBe("Version 999.0.0 is available.");
        about.LatestReleaseUrl.ShouldBe("https://example.test/latest");
    }

    private static HttpResponseMessage Releases(string tagName) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""
            [
                {
                    "tag_name": "{{tagName}}",
                    "name": "Latest",
                    "html_url": "https://example.test/latest",
                    "draft": false
                }
            ]
            """),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}
