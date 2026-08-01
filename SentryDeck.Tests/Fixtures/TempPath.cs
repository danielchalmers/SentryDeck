using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// A scratch directory that deletes itself, so tests needing real files on disk read as a straight line instead of wrapping their assertions in try/finally.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string suffix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a subdirectory and returns its full path.</summary>
    public string CreateSubdirectory(string name) =>
        Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    /// <summary>Writes a file directly under this directory and returns its full path.</summary>
    public string Write(string name, byte[] content = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, content ?? []);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>
/// A single scratch file that deletes itself, whether this fixture wrote it or the code under test did.
/// Pass no content to reserve a path without creating anything, which is what a test wanting to assert that something else created or removed the file needs.
/// </summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(byte[] content = null, string extension = ".mp4")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}{extension}");
        if (content is not null)
        {
            File.WriteAllBytes(Path, content);
        }
    }

    public string Path { get; }

    // File.Delete is a no-op on a path that doesn't exist, so this covers the reserved-path case too.
    public void Dispose() => File.Delete(Path);
}
