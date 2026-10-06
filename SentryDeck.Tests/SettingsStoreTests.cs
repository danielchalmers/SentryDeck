using System.IO;
using System.Text;

namespace SentryDeck.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void LoadPickedFolders_NothingSavedYet_ReturnsNone()
    {
        using var temp = new TempDirectory();

        new SettingsStore(Path.Combine(temp.Path, "settings.json")).LoadPickedFolders().ShouldBeEmpty();
    }

    [Fact]
    public void SavePickedFolders_ThenLoadInANewStore_ReturnsTheSameFolders()
    {
        using var temp = new TempDirectory();

        // The app's own folder under %LOCALAPPDATA% doesn't exist until the first save creates it.
        var path = Path.Combine(temp.Path, "SentryDeck", "settings.json");
        new SettingsStore(path).SavePickedFolders([@"D:\TeslaCam", @"E:\Footage\TeslaCam"]);

        // A new store reads them from disk, as the next launch would.
        new SettingsStore(path).LoadPickedFolders().ShouldBe([@"D:\TeslaCam", @"E:\Footage\TeslaCam"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"PickedFolders\": 5}")]
    public void LoadPickedFolders_FileIsDamaged_ReturnsNone(string content)
    {
        using var temp = new TempDirectory();
        var path = temp.Write("settings.json", Encoding.UTF8.GetBytes(content));

        // A damaged file only costs picking the folder again, but throwing here would stop the app from starting.
        new SettingsStore(path).LoadPickedFolders().ShouldBeEmpty();
    }

    [Fact]
    public void SavePickedFolders_FileCantBeWritten_DoesNotThrow()
    {
        using var temp = new TempDirectory();

        // A file where the settings folder belongs makes the save fail.
        var notAFolder = temp.Write("SentryDeck");
        var store = new SettingsStore(Path.Combine(notAFolder, "settings.json"));

        // Failing to remember the pick must not keep the folder just picked from opening.
        Should.NotThrow(() => store.SavePickedFolders([@"D:\TeslaCam"]));
        store.LoadPickedFolders().ShouldBeEmpty();
    }
}
