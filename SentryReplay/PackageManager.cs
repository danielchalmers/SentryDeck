using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Serilog;

namespace SentryReplay;

public static class PackageManager
{
    private const string FFmpegReleaseBranch = "8.1";
    private static readonly string FFmpegBinFolderName = $"ffmpeg-{FFmpegReleaseBranch}-bin";

    private static string FFmpegInstallRoot => AppContext.BaseDirectory;

    private static async Task<long> DownloadFile(string url, string savePath)
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var fileStream = File.Create(savePath);
        await response.Content.CopyToAsync(fileStream);

        return fileStream.Length;
    }

    private static int ExtractFFmpegBin(string zipFilePath, string destinationBinPath, string archiveRoot)
    {
        if (Directory.Exists(destinationBinPath))
        {
            Directory.Delete(destinationBinPath, true);
        }

        Directory.CreateDirectory(destinationBinPath);
        var extractedFileCount = 0;

        var binPrefix = $"{archiveRoot}/bin/";
        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = entry.FullName[binPrefix.Length..];
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var outputPath = Path.Combine(destinationBinPath, relativePath);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            entry.ExtractToFile(outputPath, true);
            extractedFileCount++;
        }

        return extractedFileCount;
    }

    public static async Task DownloadAndExtractFFmpeg()
    {
        var destinationBinPath = Path.Combine(FFmpegInstallRoot, FFmpegBinFolderName);
        var url = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegReleaseBranch}-latest-win64-gpl-shared-{FFmpegReleaseBranch}.zip",
            Architecture.Arm64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegReleaseBranch}-latest-winarm64-gpl-shared-{FFmpegReleaseBranch}.zip",
            _ => throw new NotSupportedException($"FFmpeg download is not supported for {RuntimeInformation.ProcessArchitecture}."),
        };
        var tempPath = Path.GetTempFileName();
        var archiveRoot = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Log.Information(
                "Downloading FFmpeg. Url={Url}; TempPath={TempPath}; Architecture={Architecture}",
                url,
                tempPath,
                RuntimeInformation.ProcessArchitecture);
            var bytesDownloaded = await DownloadFile(url, tempPath);
            Log.Information(
                "Downloaded FFmpeg archive. TempPath={TempPath}; Bytes={Bytes}; ElapsedMs={ElapsedMs}",
                tempPath,
                bytesDownloaded,
                stopwatch.ElapsedMilliseconds);

            Log.Information(
                "Extracting FFmpeg binaries. TempPath={TempPath}; Destination={Destination}; ArchiveRoot={ArchiveRoot}",
                tempPath,
                destinationBinPath,
                archiveRoot);
            var extractedFileCount = ExtractFFmpegBin(tempPath, destinationBinPath, archiveRoot);

            Log.Information(
                "FFmpeg binaries are ready. Destination={Destination}; ExtractedFileCount={ExtractedFileCount}; ElapsedMs={ElapsedMs}",
                destinationBinPath,
                extractedFileCount,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static string FindFFmpegDirectory()
    {
        var binPath = Path.Combine(FFmpegInstallRoot, FFmpegBinFolderName);

        if (File.Exists(Path.Combine(binPath, "ffmpeg.exe")))
        {
            return binPath;
        }

        return null;
    }
}
