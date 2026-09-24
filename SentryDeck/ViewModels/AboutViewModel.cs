using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace SentryDeck;

/// <summary>
/// What the About and help page shows: this build's version and environment, and whether a newer release is out.
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly UpdateService _updateService = new();

    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public string FileVersion => FormatVersion(CurrentVersion);

    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

    public string OsDescription => RuntimeInformation.OSDescription;

    public string ExecutablePath => Environment.ProcessPath;

    public bool HasUpdateBadge => IsUpdateAvailable;

    public string LatestVersionText => LatestRelease is null ? "Unknown" : FormatVersion(LatestRelease.Version);

    public string LatestReleaseUrl => LatestRelease?.ReleaseUrl ?? UpdateService.ReleasesPageUrl;

    public string ReleasesPageUrl => UpdateService.ReleasesPageUrl;

    public string UpdateStatusTitle => IsUpdateAvailable
        ? "Update available"
        : "You're up to date";

    public string UpdateStatusDetails => IsUpdateAvailable
        ? $"Version {LatestVersionText} is available."
        : "No newer release was found.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBadge))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseUrl))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetails))]
    private UpdateRelease _latestRelease;

    public async Task CheckForUpdatesAsync()
    {
        var result = await _updateService.CheckForUpdateAsync(CurrentVersion);
        LatestRelease = result.LatestRelease;
        IsUpdateAvailable = result.IsUpdateAvailable;

        Log.Information(
            "Checked for updates. CurrentVersion={CurrentVersion}; LatestVersion={LatestVersion}; IsUpdateAvailable={IsUpdateAvailable}",
            FormatVersion(CurrentVersion),
            LatestVersionText,
            IsUpdateAvailable);
    }

    private static string FormatVersion(Version version)
    {
        if (version is null)
        {
            return "Unknown";
        }

        if (version.Revision >= 0)
        {
            return version.ToString(4);
        }

        return version.Build >= 0
            ? version.ToString(3)
            : version.ToString(2);
    }
}
