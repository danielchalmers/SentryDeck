using System.Net;
using System.Net.Http;

namespace SentryReplay.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("  v1.2.3  ", 1, 2, 3)]
    public void TryParseVersion_AcceptsVersionTags(string tagName, int major, int minor, int build)
    {
        var version = UpdateService.TryParseVersion(tagName);

        version.ShouldNotBeNull();
        version.Major.ShouldBe(major);
        version.Minor.ShouldBe(minor);
        version.Build.ShouldBe(build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("version1")]
    [InlineData("v1.2.3-beta1")]
    [InlineData("1.2.3+build")]
    public void TryParseVersion_IgnoresTagsVersionCannotParseDirectly(string tagName)
    {
        var version = UpdateService.TryParseVersion(tagName);

        version.ShouldBeNull();
    }

    [Fact]
    public void GetLatestRelease_ReturnsHighestParseableNonDraftRelease()
    {
        var payload = """
        [
            {
                "tag_name": "v1.2.3-beta1",
                "name": "Beta",
                "html_url": "https://example.test/beta",
                "draft": false
            },
            {
                "tag_name": "v2.0.0",
                "name": "Draft",
                "html_url": "https://example.test/draft",
                "draft": true
            },
            {
                "tag_name": "v1.4.0",
                "name": "Current",
                "html_url": "https://example.test/current",
                "draft": false
            },
            {
                "tag_name": "1.3.0",
                "name": "Previous",
                "html_url": "https://example.test/previous",
                "draft": false
            }
        ]
        """;

        var release = UpdateService.GetLatestRelease(payload);

        release.ShouldNotBeNull();
        release.Version.ShouldBe(new Version(1, 4, 0));
        release.Name.ShouldBe("Current");
        release.ReleaseUrl.ShouldBe("https://example.test/current");
    }

    [Fact]
    public void IsUpdateAvailable_ReturnsTrueOnlyForHigherLatestVersion()
    {
        UpdateService.IsUpdateAvailable(new Version(1, 2, 3), new Version(1, 2, 4)).ShouldBeTrue();
        UpdateService.IsUpdateAvailable(new Version(1, 2, 3), new Version(1, 2, 3)).ShouldBeFalse();
        UpdateService.IsUpdateAvailable(new Version(1, 2, 3), new Version(1, 2, 2)).ShouldBeFalse();
        UpdateService.IsUpdateAvailable(null, new Version(1, 2, 4)).ShouldBeFalse();
        UpdateService.IsUpdateAvailable(new Version(1, 2, 3), null).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_UsesLatestRelease()
    {
        var payload = """
        [
            {
                "tag_name": "v1.2.4",
                "name": "Latest",
                "html_url": "https://example.test/latest",
                "draft": false
            }
        ]
        """;
        var service = new UpdateService(new HttpClient(new StubHandler(payload)));

        var result = await service.CheckForUpdateAsync(new Version(1, 2, 3));

        result.IsUpdateAvailable.ShouldBeTrue();
        result.LatestRelease.ShouldNotBeNull();
        result.LatestRelease.Version.ShouldBe(new Version(1, 2, 4));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ReturnsNullWhenGitHubRequestFails()
    {
        var service = new UpdateService(new HttpClient(new StubHandler("{}", HttpStatusCode.InternalServerError)));

        var release = await service.GetLatestReleaseAsync();

        release.ShouldBeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly HttpStatusCode _statusCode;

        public StubHandler(string payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _payload = payload;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri.ToString().ShouldBe(UpdateService.ReleasesApiUrl);

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload),
            });
        }
    }
}
