using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Serilog;

namespace SentryReplay;

public static class PackageManager
{
    private const string FFmpegVersion = "7.1";
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

    private static void ExtractFFmpegBin(string zipFilePath, string destinationBinPath, string archiveRoot)
    {
        if (Directory.Exists(destinationBinPath))
        {
            Directory.Delete(destinationBinPath, true);
        }

        Directory.CreateDirectory(destinationBinPath);

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
        }
    }

    public static async Task DownloadAndExtractFFmpeg()
    {
        var outputFolder = Path.GetFullPath(".");
        var destinationBinPath = Path.Combine(outputFolder, FFmpegBinFolderName);
        var url = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegVersion}-latest-win64-gpl-shared-{FFmpegVersion}.zip",
            Architecture.Arm64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegVersion}-latest-winarm64-gpl-{FFmpegVersion}.zip",
            _ => throw new NotSupportedException($"FFmpeg download is not supported for {RuntimeInformation.ProcessArchitecture}."),
        };
        var tempPath = Path.GetTempFileName();
        var archiveRoot = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);

        Log.Information($"Downloading ffmpeg to {tempPath} from {url}");
        await DownloadFile(url, tempPath);

        Log.Information($"Extracting ffmpeg bin to {destinationBinPath}");
        ExtractFFmpegBin(tempPath, destinationBinPath, archiveRoot);

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
