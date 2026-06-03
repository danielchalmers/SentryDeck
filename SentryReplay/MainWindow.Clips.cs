using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SentryReplay.Data;
using Serilog;

namespace SentryReplay;

public partial class MainWindow
{
    private void LoadClips(IEnumerable<string> roots)
    {
        ClearError();
        AllClips.Clear();

        var rootList = roots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() ?? [];
        if (rootList.Count == 0)
        {
            Log.Information("No dashcam roots found");
            ShowError("No Dashcam Folders Found",
                "Click 'Select Folder' to choose a folder containing Tesla dashcam footage (TeslaCam folder).",
                canDismiss: true);
            OnPropertyChanged(nameof(FilteredClips));
            OnPropertyChanged(nameof(HasNoClipSelected));
            return;
        }

        Log.Information("Loading dashcam clips. RootCount={RootCount}; Roots={Roots}", rootList.Count, rootList);
        var totalStopwatch = Stopwatch.StartNew();
        var failedRoots = 0;

        foreach (var root in rootList)
        {
            var rootStopwatch = Stopwatch.StartNew();
            Log.Debug("Scanning dashcam root. Root={Root}", root);

            try
            {
                var storage = CamStorage.Map(root);
                AllClips.AddRange(storage.Clips);
                Log.Information(
                    "Scanned dashcam root. Root={Root}; ClipCount={ClipCount}; ElapsedMs={ElapsedMs}",
                    root,
                    storage.Clips.Count,
                    rootStopwatch.ElapsedMilliseconds);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedRoots++;
                Log.Error(ex, "Access denied while loading dashcam root. Root={Root}", root);
                ShowError("Access Denied", $"Cannot access folder: {root}\n\nCheck that you have permission to read this location.");
            }
            catch (Exception ex)
            {
                failedRoots++;
                Log.Error(ex, "Failed to load dashcam root. Root={Root}", root);
                ShowError("Error Loading Clips", $"Failed to load clips from:\n{root}\n\nError: {ex.Message}");
            }
        }

        _playerController?.LoadClips(AllClips);

        OnPropertyChanged(nameof(FilteredClips));
        OnPropertyChanged(nameof(HasNoClipSelected));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        Log.Information(
            "Finished loading dashcam clips. ClipCount={ClipCount}; RootCount={RootCount}; FailedRootCount={FailedRootCount}; ElapsedMs={ElapsedMs}",
            AllClips.Count,
            rootList.Count,
            failedRoots,
            totalStopwatch.ElapsedMilliseconds);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Log.Debug("Opening folder picker");

        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select a folder containing Tesla dashcam footage (TeslaCam folder)",
        };

        if (dialog.ShowDialog() == true)
        {
            Log.Information(
                "User selected dashcam folders. FolderCount={FolderCount}; Folders={Folders}",
                dialog.FolderNames.Length,
                dialog.FolderNames);

            if (_playerController is not null)
            {
                await _playerController.StopAsync();
            }

            LoadClips(dialog.FolderNames);
        }
        else
        {
            Log.Debug("Folder picker canceled");
        }
    }
    private static bool CanUseClip(CamClip clip)
    {
        return clip is not null;
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void OpenClipFolder(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        if (!Directory.Exists(clip.FullPath))
        {
            ShowError("Clip Folder Not Found", $"Could not find folder:\n{clip.FullPath}");
            return;
        }

        Process.Start(new ProcessStartInfo(clip.FullPath)
        {
            UseShellExecute = true,
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipPath(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        Clipboard.SetText(clip.FullPath);
    }

    [RelayCommand(CanExecute = nameof(CanUseClip))]
    private void CopyClipName(CamClip clip)
    {
        if (clip is null)
        {
            return;
        }

        Clipboard.SetText(clip.Name);
    }

    partial void OnSelectedClipChanged(CamClip value)
    {
        if (value is not null)
        {
            Log.Debug(
                "Selected clip changed. ClipName={ClipName}; ClipPath={ClipPath}",
                value.Name,
                value.FullPath);
            _ = PlaySelectedClipAsync();
        }
    }
}
