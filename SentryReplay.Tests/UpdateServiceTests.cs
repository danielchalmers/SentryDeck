using System.Net;
using System.Net.Http;
using System.Reflection;
using Shouldly;

namespace SentryReplay.Tests;

/// <summary>
/// Tests for checking GitHub releases for app updates.
/// </summary>
public class UpdateServiceTests
{
    [Fact]
    public async Task GetAvailableUpdateAsync_ReturnsNewestHigherVersion()
    {
        // Arrange
        var service = CreateService("""
            [
              { "tag_name": "v0.7.0", "html_url": "https://example.com/v0.7.0", "draft": false, "prerelease": false },
              { "tag_name": "v0.6.0", "html_url": "https://example.com/v0.6.0", "draft": false, "prerelease": false }
            ]
            """);

        // Act
        var update = await service.GetAvailableUpdateAsync(new Version(0, 6, 0));

        // Assert
        update.ShouldNotBeNull();
        update.Version.ShouldBe(new Version(0, 7, 0));
        update.ReleaseUri.ShouldBe(new Uri("https://example.com/v0.7.0"));
    }

    [Fact]
    public async Task GetAvailableUpdateAsync_IgnoresTagsThatVersionCannotParse()
    {
        // Arrange
        var service = CreateService("""
            [
              { "tag_name": "v1.2.3-beta1", "html_url": "https://example.com/v1.2.3-beta1", "draft": false, "prerelease": false },
              { "tag_name": "v1.2.3", "html_url": "https://example.com/v1.2.3", "draft": false, "prerelease": false }
            ]
            """);

        // Act
        var update = await service.GetAvailableUpdateAsync(new Version(1, 2, 2));

        // Assert
        update.ShouldNotBeNull();
        update.Version.ShouldBe(new Version(1, 2, 3));
        update.ReleaseUri.ShouldBe(new Uri("https://example.com/v1.2.3"));
    }

    [Fact]
    public async Task GetAvailableUpdateAsync_IgnoresDraftAndPrereleaseReleases()
    {
        // Arrange
        var service = CreateService("""
            [
              { "tag_name": "v0.9.0", "html_url": "https://example.com/v0.9.0", "draft": true, "prerelease": false },
              { "tag_name": "v0.8.0", "html_url": "https://example.com/v0.8.0", "draft": false, "prerelease": true },
              { "tag_name": "v0.7.0", "html_url": "https://example.com/v0.7.0", "draft": false, "prerelease": false }
            ]
            """);

        // Act
        var update = await service.GetAvailableUpdateAsync(new Version(0, 6, 0));

        // Assert
        update.ShouldNotBeNull();
        update.Version.ShouldBe(new Version(0, 7, 0));
    }

    [Fact]
    public async Task GetAvailableUpdateAsync_ReturnsNullWhenAlreadyUpToDate()
    {
        // Arrange
        var service = CreateService("""
            [
              { "tag_name": "v0.7.0", "html_url": "https://example.com/v0.7.0", "draft": false, "prerelease": false }
            ]
            """);

        // Act
        var update = await service.GetAvailableUpdateAsync(new Version(0, 7, 0));

        // Assert
        update.ShouldBeNull();
    }

    [Fact]
    public void GetCurrentVersion_ReturnsAssemblyVersion()
    {
        // Act
        var version = UpdateService.GetCurrentVersion(Assembly.GetExecutingAssembly());

        // Assert
        version.ShouldBe(Assembly.GetExecutingAssembly().GetName().Version);
    }

    private static UpdateService CreateService(string json)
    {
        var client = new HttpClient(new StubHttpMessageHandler(json));
        return new UpdateService(client);
    }

    private sealed class StubHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
