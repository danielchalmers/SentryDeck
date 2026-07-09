using System.Text;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Heuristics for Tesla's encrypted dashcam recordings. Software update 2026.20 turns on
/// "Encrypt Dashcam Recordings" by default (Controls > Safety), writing AES-encrypted
/// containers to the USB drive instead of plain MP4s, so the files no longer begin with an
/// ISO-BMFF box header. Decryption keys are only obtainable from Tesla's servers via the
/// owner's account (dashcam.tesla.com), so the app can detect the state but not play it.
/// </summary>
public static class EncryptedClipDetector
{
    /// <summary>
    /// Top-level box types an unencrypted recording can plausibly start with. Tesla files start
    /// with <c>ftyp</c>; the rest keep the sniff from misreporting other muxers' output — a file
    /// starting with any of these is ordinary video (playable or merely corrupt), not encrypted.
    /// </summary>
    private static readonly string[] KnownLeadingBoxTypes =
    [
        "ftyp",
        "styp",
        "moov",
        "mdat",
        "free",
        "skip",
        "wide",
        "pdin",
        "sidx",
        "uuid",
    ];

    /// <summary>
    /// True when the clip's front-camera files are all present with content but none starts like
    /// an MP4 — the signature of a drive written with encryption enabled. A merely corrupt or
    /// truncated clip still has a valid <c>ftyp</c> header on at least some chunks, so it stays
    /// on the ordinary unreadable-file path.
    /// </summary>
    public static bool LooksEncrypted(CamClip clip)
    {
        if (clip is null || clip.Chunks.Count == 0)
        {
            return false;
        }

        var sawFrontFile = false;

        foreach (var chunk in clip.Chunks)
        {
            if (!chunk.Files.TryGetValue(CameraNames.Front, out var frontFile))
            {
                continue;
            }

            sawFrontFile = true;

            if (!LooksEncrypted(frontFile.FullPath))
            {
                return false;
            }
        }

        return sawFrontFile;
    }

    /// <summary>
    /// True when the file has content but does not start with a recognizable MP4 box.
    /// </summary>
    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                // Shorter than one box header: a truncated write, not an encrypted container
                // (those carry a fixed header plus at least one 4 KiB payload chunk).
                return false;
            }

            var leadingBoxType = Encoding.ASCII.GetString(header[4..]);
            return !KnownLeadingBoxTypes.Contains(leadingBoxType);
        }
        catch (Exception ex)
        {
            // Unreadable at the filesystem level (missing, locked, ...) is not an encryption
            // signal; let the ordinary unreadable-file handling describe it.
            Log.Debug(ex, "Could not sniff file header for encryption. File={File}", path);
            return false;
        }
    }
}
