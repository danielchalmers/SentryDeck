using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;

namespace SentryReplay.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_WhenHigherStableReleaseExists_ReturnsUpdate()
    {
        using var client = CreateHttpClient("""
            [
              { "tag_name": "v0.8.0", "draft": false, "prerelease": false },
              { "tag_name": "v0.9.0-beta.1", "draft": false, "prerelease": true }
            ]
            """);
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("0.7.0+abc");

        result.IsUpdateAvailable.ShouldBeTrue();
        result.LatestTagName.ShouldBe("v0.8.0");
        result.LatestVersion.ShouldBe(new Version(0, 8, 0));
        result.ReleaseUrl.ShouldBe(UpdateService.LatestReleasePageUrl);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenCurrentVersionIsLatest_ReturnsNoUpdate()
    {
        using var client = CreateHttpClient("""
            [
              { "tag_name": "v0.8.0", "draft": false, "prerelease": false },
              { "tag_name": "v0.7.0", "draft": false, "prerelease": false }
            ]
            """);
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("0.8.0.0");

        result.ShouldBe(UpdateCheckResult.None);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_IgnoresDraftAndPrereleaseEntries()
    {
        using var client = CreateHttpClient("""
            [
              { "tag_name": "v0.9.0", "draft": true, "prerelease": false },
              { "tag_name": "v0.8.0-beta.1", "draft": false, "prerelease": true },
              { "tag_name": "v0.7.0", "draft": false, "prerelease": false }
            ]
            """);
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("0.7.0");

        result.ShouldBe(UpdateCheckResult.None);
    }

    private static HttpClient CreateHttpClient(string responseBody)
    {
        return new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        }))
        {
            BaseAddress = new Uri("https://api.github.com"),
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.ShouldBe(new Uri("https://api.github.com/repos/danielchalmers/SentryReplay/releases"));
            return Task.FromResult(handler(request));
        }
    }
}
