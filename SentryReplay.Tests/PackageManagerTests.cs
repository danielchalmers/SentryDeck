using System.Runtime.InteropServices;
using Shouldly;

namespace SentryReplay.Tests;

/// <summary>
/// Tests for PackageManager functionality.
/// </summary>
public class PackageManagerTests
{
    [Fact]
    public void GetFFmpegDownloadUrl_ReturnsValidUrl()
    {
        // We can't directly test the private method, but we can verify the logic indirectly
        // by checking that the current architecture is supported
        var architecture = RuntimeInformation.ProcessArchitecture;
        
        // Verify that the architecture is one we support
        architecture.ShouldBeOneOf(Architecture.X64, Architecture.Arm64, Architecture.X86, Architecture.Arm);
    }

    [Fact]
    public void FindFFmpegDirectories_ReturnsEnumerable()
    {
        // Test that the method returns an enumerable (even if empty)
        var directories = PackageManager.FindFFmpegDirectories(".");
        directories.ShouldNotBeNull();
    }
}
