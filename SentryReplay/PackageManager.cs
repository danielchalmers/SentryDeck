using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Serilog;

namespace SentryReplay;

public static class PackageManager
{
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

    private static void ExtractZipFile(string zipFilePath, string extractPath)
    {
        ZipFile.ExtractToDirectory(zipFilePath, extractPath, true);
    }

    public static async Task DownloadAndExtractFFmpeg()
    {
        var outputFolder = Path.GetFullPath("ffmpeg");
        var url = GetFFmpegDownloadUrl();
        var tempPath = Path.GetTempFileName();

        Log.Information("Getting ffmpeg");

        Log.Debug($"Downloading ffmpeg to {tempPath} from {url}");
        await DownloadFile(url, tempPath);

        Log.Debug($"Extracting ffmpeg to {outputFolder}");
        ExtractZipFile(tempPath, outputFolder);

        File.Delete(tempPath);
    }

    private static string GetFFmpegDownloadUrl()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        
        return architecture switch
        {
            Architecture.Arm64 => "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-gpl-shared.zip",
            Architecture.X64 => "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip",
            _ => "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip", // Default to x64
        };
    }

    public static IEnumerable<string> FindFFmpegDirectories(string searchDirectory = ".")
    {
        foreach (var path in Directory.EnumerateFiles(searchDirectory, "ffmpeg.exe", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(Path.GetDirectoryName(path));
        }
    }
}
