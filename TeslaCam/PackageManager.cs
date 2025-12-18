using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Serilog;

namespace TeslaCam;

public static class PackageManager
{
    private static async Task DownloadFile(string url, string savePath)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        
        using var fileStream = File.Create(savePath);
        await response.Content.CopyToAsync(fileStream);
    }

    private static void ExtractZipFile(string zipFilePath, string extractPath)
    {
        // Extract and flatten nested directories - the zip contains ffmpeg-7.0-full_build-shared/bin/*.dll and ffmpeg.exe
        using var archive = ZipFile.OpenRead(zipFilePath);
        
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            
            // Find bin directory in the zip structure
            var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var binIndex = entryPath.IndexOf("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            
            if (binIndex >= 0)
            {
                // Extract files from bin directory to root of extractPath
                var relativePath = entryPath.Substring(binIndex + 4); // Skip "bin\\"
                var destPath = Path.Combine(extractPath, relativePath);
                
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
    }

    public static async Task DownloadAndExtractFFmpeg()
    {
        var outputFolder = Path.GetFullPath("ffmpeg");
        var url = "https://github.com/GyanD/codexffmpeg/releases/download/7.0/ffmpeg-7.0-full_build-shared.zip";
        var tempPath = Path.GetTempFileName();

        try
        {
            Log.Information("Downloading FFmpeg...");

            Log.Debug($"Downloading FFmpeg to {tempPath} from {url}");
            await DownloadFile(url, tempPath);

            Log.Information("Extracting FFmpeg...");
            Log.Debug($"Extracting FFmpeg to {outputFolder}");
            
            if (Directory.Exists(outputFolder))
                Directory.Delete(outputFolder, true);
            Directory.CreateDirectory(outputFolder);
            
            ExtractZipFile(tempPath, outputFolder);
            
            Log.Information("FFmpeg downloaded and extracted successfully");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static IEnumerable<string> FindFFmpegDirectories(string searchDirectory = ".")
    {
        foreach (var path in Directory.EnumerateFiles(searchDirectory, "ffmpeg.exe", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(Path.GetDirectoryName(path));
        }
    }
}
