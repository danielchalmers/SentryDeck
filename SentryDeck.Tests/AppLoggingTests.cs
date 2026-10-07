using System.IO;

namespace SentryDeck.Tests;

public sealed class AppLoggingTests
{
    [Fact]
    public void CreateLogger_WhileAnotherInstanceHasTodaysLogOpen_AppendsToTheSameFile()
    {
        // Each instance that opened a numbered copy of today's file pushed an earlier day's log out of the seven kept.
        using var logs = new TempDirectory();
        var logPath = Path.Combine(logs.Path, "log-.txt");

        using (var first = App.CreateLogger(logPath))
        using (var second = App.CreateLogger(logPath))
        {
            first.Information("From the first instance");
            second.Information("From the second instance");
        }

        var file = Directory.GetFiles(logs.Path).ShouldHaveSingleItem();
        var text = File.ReadAllText(file);
        text.ShouldContain("From the first instance");
        text.ShouldContain("From the second instance");
    }

    [Fact]
    public void CreateLogger_WritingToTheSharedFile_TagsEachLineWithTheProcessId()
    {
        // Several instances append to one file, so a line that doesn't name its process can't be traced to the window that wrote it.
        using var logs = new TempDirectory();
        var logPath = Path.Combine(logs.Path, "log-.txt");

        using (var logger = App.CreateLogger(logPath))
        {
            logger.Information("First line");
            logger.Warning("Second line");
        }

        var lines = File.ReadAllLines(Directory.GetFiles(logs.Path).ShouldHaveSingleItem());
        lines.Length.ShouldBe(2);
        lines[0].ShouldMatch($@"^\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}} [+-]\d{{2}}:\d{{2}} \[INF\] \[{Environment.ProcessId}\] First line$");
        lines[1].ShouldEndWith($"[WRN] [{Environment.ProcessId}] Second line");
    }
}
