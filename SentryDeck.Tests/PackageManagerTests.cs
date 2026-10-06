using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;

namespace SentryDeck.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private const string ArchiveRoot = "ffmpeg-n9.0-latest-win64-gpl-shared-9.0";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Nested below the scratch root so everything a test writes lands inside the directory Dispose deletes.
    private string DestinationBinPath => Path.Combine(_root, "install", "ffmpeg-bin");

    private string WriteArchive(params string[] entryNames)
    {
        var zipPath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var entryName in entryNames)
        {
            var entry = archive.CreateEntry(entryName);

            // A name ending in "/" is a bare directory entry, which real FFmpeg archives carry.
            if (entryName.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            stream.Write("payload"u8);
        }

        return zipPath;
    }

    [Fact]
    public void ExtractFFmpegBin_SkipsEntriesOutsideTheBinPrefix()
    {
        var zipPath = WriteArchive(
            $"{ArchiveRoot}/bin/",
            $"{ArchiveRoot}/bin/ffmpeg.exe",
            $"{ArchiveRoot}/bin/sub/avcodec.dll",
            $"{ArchiveRoot}/doc/README");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        // Only the two real binaries count.
        // The returned count is the only signal the caller logs, so a bare directory entry inflating it would make an unusable install look successful.
        extracted.ShouldBe(2);
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
        Directory.GetFiles(DestinationBinPath, "README", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractFFmpegBin_MatchesThePrefixCaseInsensitively()
    {
        // The prefix is derived from the download URL, not from the archive, so a casing mismatch between the two would otherwise leave an empty bin folder and no FFmpeg at all.
        var zipPath = WriteArchive($"{ArchiveRoot.ToUpperInvariant()}/BIN/ffmpeg.exe");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        extracted.ShouldBe(1);
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
    }

    [Fact]
    public void ExtractFFmpegBin_RejectsEntriesThatEscapeTheDestination()
    {
        // Entry names come verbatim out of a downloaded archive and the destination sits next to the app's own binaries, so a "../" walk must be dropped rather than written.
        // Forward slashes because that is what the ZIP format mandates and what the real builds emit.
        var zipPath = WriteArchive(
            $"{ArchiveRoot}/bin/ffmpeg.exe",
            $"{ArchiveRoot}/bin/../../evil.dll");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        extracted.ShouldBe(1);
        Directory.GetFiles(_root, "evil.dll", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void RemoveSupersededFFmpegDirectories_DeletesOtherBranchesAndKeepsTheCurrentOne()
    {
        // Each FFmpeg release branch installs into a folder of its own, so bumping the branch strands the previous build (about 200 MB unpacked) next to the app with nothing that will ever load it again.
        var installRoot = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        Directory.CreateDirectory(Path.Combine(installRoot, "ffmpeg-9.0-bin"));
        Directory.CreateDirectory(Path.Combine(installRoot, "ffmpeg-8.1-bin"));
        Directory.CreateDirectory(Path.Combine(installRoot, "ffmpeg-7.1-bin"));
        File.WriteAllText(Path.Combine(installRoot, "ffmpeg-8.1-bin", "avcodec-62.dll"), "stale");

        var removed = PackageManager.RemoveSupersededFFmpegDirectories(installRoot, "ffmpeg-9.0-bin");

        removed.ShouldBe(2);
        Directory.Exists(Path.Combine(installRoot, "ffmpeg-9.0-bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(installRoot, "ffmpeg-8.1-bin")).ShouldBeFalse();
        Directory.Exists(Path.Combine(installRoot, "ffmpeg-7.1-bin")).ShouldBeFalse();
    }

    [Fact]
    public void RemoveSupersededFFmpegDirectories_LeavesEverythingElseAlone()
    {
        // The install root is the app's own folder, so the match has to stay narrow enough that nothing but an FFmpeg install can ever be caught by it.
        var installRoot = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        Directory.CreateDirectory(Path.Combine(installRoot, "ffmpeg-9.0-bin"));
        Directory.CreateDirectory(Path.Combine(installRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(installRoot, "ffmpeg-notes"));
        Directory.CreateDirectory(Path.Combine(installRoot, "runtimes"));

        var removed = PackageManager.RemoveSupersededFFmpegDirectories(installRoot, "ffmpeg-9.0-bin");

        removed.ShouldBe(0);
        Directory.Exists(Path.Combine(installRoot, "logs")).ShouldBeTrue();
        Directory.Exists(Path.Combine(installRoot, "ffmpeg-notes")).ShouldBeTrue();
        Directory.Exists(Path.Combine(installRoot, "runtimes")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("dropped mid-download")]
    [InlineData("timed out")]
    public void DescribeDownloadFailure_NetworkError_SaysWhatToCheckInsteadOfTheSocketError(string failure)
    {
        Exception exception = failure switch
        {
            "refused" => new HttpRequestException("No connection could be made because the target machine actively refused it. (127.0.0.1:9)", new SocketException(10061)),
            "dropped mid-download" => new IOException("Unable to read data from the transport connection.", new SocketException(10054)),
            _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 1800 seconds elapsing.", new TimeoutException()),
        };

        var description = PackageManager.DescribeDownloadFailure(exception);

        description.ShouldContain("online");
        description.ShouldContain("proxy or firewall");
        description.ShouldNotContain(exception.Message);
    }

    [Fact]
    public void DescribeDownloadFailure_FolderNotWritable_SuggestsMovingTheApp()
    {
        var description = PackageManager.DescribeDownloadFailure(new UnauthorizedAccessException("Access to the path is denied."));

        description.ShouldContain("folder you can write to");
    }

    [Fact]
    public void DescribeDownloadFailure_UnexpectedError_KeepsItsMessageAsTheOnlyClue()
    {
        var description = PackageManager.DescribeDownloadFailure(new InvalidDataException("End of Central Directory record could not be found."));

        description.ShouldContain("End of Central Directory record could not be found.");
    }

    [Fact]
    public void DescribeDownloadFailure_EndsWithHowToInstallFFmpegByHand()
    {
        // Someone behind a firewall that blocks GitHub can never download it from the app, so the message has to say where a manually downloaded build goes.
        var description = PackageManager.DescribeDownloadFailure(new HttpRequestException("Connection refused"));

        description.ShouldContain(Path.Combine(AppContext.BaseDirectory, "ffmpeg-9.0-bin"));
    }

    [Fact]
    public void ExtractFFmpegBin_ClearsAnExistingDestination()
    {
        // A leftover DLL from an earlier FFmpeg release must not survive alongside the new ones; Flyleaf loads whatever is in this folder and a mixed set fails at load time.
        Directory.CreateDirectory(DestinationBinPath);
        var stalePath = Path.Combine(DestinationBinPath, "avcodec-60.dll");
        File.WriteAllText(stalePath, "stale");

        var zipPath = WriteArchive($"{ArchiveRoot}/bin/ffmpeg.exe");
        PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        File.Exists(stalePath).ShouldBeFalse();
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
    }
}
