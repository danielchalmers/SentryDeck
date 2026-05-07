using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SentryReplay;

public sealed class UpdateService(HttpClient client = null)
{
    public static readonly Uri ReleasesApiUri = new("https://api.github.com/repos/danielchalmers/SentryReplay/releases");
    public static readonly Uri ReleasesPageUri = new("https://github.com/danielchalmers/SentryReplay/releases");
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private readonly HttpClient _client = client ?? SharedClient;

    public static Version GetCurrentVersion(Assembly assembly = null)
    {
        return (assembly ?? typeof(UpdateService).Assembly).GetName().Version ?? new Version(0, 0);
    }

    public async Task<AvailableUpdate> GetAvailableUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        var releases = await _client.GetFromJsonAsync<List<GitHubRelease>>(ReleasesApiUri, cancellationToken) ?? [];

        return releases
            .Where(release => !release.Draft && !release.Prerelease)
            .Select(release => new
            {
                Release = release,
                Version = ParseVersion(release.TagName),
            })
            .Where(release => release.Version is not null && release.Version > currentVersion)
            .OrderByDescending(release => release.Version)
            .Select(release => new AvailableUpdate(
                release.Version,
                Uri.TryCreate(release.Release.HtmlUrl, UriKind.Absolute, out var releaseUri)
                    ? releaseUri
                    : ReleasesPageUri))
            .FirstOrDefault();
    }

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SentryReplay/{GetCurrentVersion()}");
        return client;
    }

    private static Version ParseVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        var versionText = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? tagName[1..]
            : tagName;

        return Version.TryParse(versionText, out var version)
            ? version
            : null;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
}

public sealed record AvailableUpdate(Version Version, Uri ReleaseUri);
