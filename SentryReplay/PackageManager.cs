using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Serilog;

namespace SentryReplay;

public static class PackageManager
{
    private const string FFmpegVersion = "7.1";
    private static readonly string FFmpegArchiveRoot = $"ffmpeg-{FFmpegVersion}-full_build-shared";
    private static readonly string FFmpegBinFolderName = $"ffmpeg-{FFmpegVersion}-bin";

    private static async Task DownloadFile(string url, string savePath)
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            using var fileStream = File.Create(savePath);
            await response.Content.CopyToAsync(fileStream);
        }
    }

    private static void ExtractFFmpegBin(string zipFilePath, string destinationBinPath)
    {
        if (Directory.Exists(destinationBinPath))
        {
            Directory.Delete(destinationBinPath, true);
        }

        Directory.CreateDirectory(destinationBinPath);

        var binPrefix = $"{FFmpegArchiveRoot}/bin/";
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
        }
    }

    public static async Task DownloadAndExtractFFmpeg()
    {
        var outputFolder = Path.GetFullPath(".");
        var destinationBinPath = Path.Combine(outputFolder, FFmpegBinFolderName);
        var url = $"https://github.com/GyanD/codexffmpeg/releases/download/{FFmpegVersion}/ffmpeg-{FFmpegVersion}-full_build-shared.zip"; // TODO: ARM64 builds?
        var tempPath = Path.GetTempFileName();

        Log.Information($"Downloading ffmpeg to {tempPath} from {url}");
        await DownloadFile(url, tempPath);

        Log.Information($"Extracting ffmpeg bin to {destinationBinPath}");
        ExtractFFmpegBin(tempPath, destinationBinPath);

        File.Delete(tempPath);
    }

    public static string FindFFmpegDirectory()
    {
        var outputFolder = Path.GetFullPath(".");
        var binPath = Path.Combine(outputFolder, FFmpegBinFolderName);

        if (File.Exists(Path.Combine(binPath, "ffmpeg.exe")))
        {
            return binPath;
        }

        return null;
    }
}
