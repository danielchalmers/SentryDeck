using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace TeslaCam;

public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/danielchalmers/SentryReplay/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/danielchalmers/SentryReplay/releases";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static string LatestReleaseUrl { get; private set; } = ReleasesPageUrl;

    /// <summary>
    /// Checks if an update is available by comparing the current version with the latest release on GitHub.
    /// Returns true if an update is available or if the current version is 0.0.0 (for testing purposes).
    /// </summary>
    public static async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            Log.Information($"Current version: {currentVersion}");

            // Always show update button for testing purposes when version is 0.0.0
            if (currentVersion == new Version(0, 0, 0))
            {
                Log.Information("Version is 0.0.0 (debug mode), showing update button for testing");
                return true;
            }

            // Fetch latest release from GitHub
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "SentryReplay");
            }
            
            var response = await _httpClient.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"Failed to check for updates: {response.StatusCode}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagNameElement))
            {
                Log.Warning("No tag_name found in GitHub API response");
                return false;
            }

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                Log.Warning("Tag name is empty");
                return false;
            }

            // Remove 'v' prefix if present
            var versionString = tagName.StartsWith('v') ? tagName[1..] : tagName;
            
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                Log.Warning($"Could not parse version from tag: {tagName}");
                return false;
            }

            // Store the latest release URL if available
            if (root.TryGetProperty("html_url", out var htmlUrlElement))
            {
                var htmlUrl = htmlUrlElement.GetString();
                if (!string.IsNullOrEmpty(htmlUrl))
                {
                    LatestReleaseUrl = htmlUrl;
                }
            }

            Log.Information($"Latest version: {latestVersion}");
            var updateAvailable = latestVersion > currentVersion;
            
            if (updateAvailable)
            {
                Log.Information($"Update available: {latestVersion}");
            }
            else
            {
                Log.Information("No update available");
            }

            return updateAvailable;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for updates");
            return false;
        }
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version ?? new Version(0, 0, 0);
    }
}
