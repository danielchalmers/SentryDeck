using System.Text;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Heuristics for Tesla's encrypted dashcam recordings.
/// Software update 2026.20 turns on "Encrypt Dashcam Recordings" by default (Controls > Safety), writing AES-encrypted containers to the USB drive instead of plain MP4s, so the files no longer begin with an ISO-BMFF box header.
/// Decryption keys are only obtainable from Tesla's servers via the owner's account (dashcam.tesla.com), so the app can detect the state but not play it.
/// </summary>
public static class EncryptedClipDetector
{
    /// <summary>
    /// Top-level box types an unencrypted recording can plausibly start with.
    /// Tesla files start with <c>ftyp</c>; the rest keep the sniff from misreporting other muxers' output: a file starting with any of these is ordinary video (playable or merely corrupt), not encrypted.
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
    /// How much of a file's start to inspect.
    /// It spans the encrypted container's 20-byte header and 16-byte IV plus most of its first 4 KiB ciphertext chunk, so the header is a small part of a real encrypted file's sample and the rest looks uniformly random.
    /// </summary>
    private const int SampleLength = 4096;

    /// <summary>
    /// The size of an ISO-BMFF box header: a 4-byte size followed by the 4-byte type.
    /// </summary>
    private const int BoxHeaderLength = 8;

    /// <summary>
    /// True when the clip's front-camera files are all present with content and every one starts with what looks like ciphertext instead of an MP4 box, the signature of a drive written with encryption enabled.
    /// A merely corrupt or truncated clip still has a valid <c>ftyp</c> header on at least some chunks, so it stays on the ordinary unreadable-file path.
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
    /// True when the file has content that neither starts with a recognizable MP4 box nor looks like filler or text, leaving ciphertext as the likely explanation.
    /// </summary>
    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            Span<byte> sample = stackalloc byte[SampleLength];
            var length = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            if (length < BoxHeaderLength)
            {
                // Shorter than one box header: a truncated write, not an encrypted container (those carry a fixed header plus at least one 4 KiB payload chunk).
                return false;
            }

            sample = sample[..length];
            var leadingBoxType = Encoding.ASCII.GetString(sample[4..BoxHeaderLength]);
            return !KnownLeadingBoxTypes.Contains(leadingBoxType) && LooksLikeCiphertext(sample);
        }
        catch (Exception ex)
        {
            // Unreadable at the filesystem level (missing, locked, ...) is not an encryption signal; let the ordinary unreadable-file handling describe it.
            Log.Debug(ex, "Could not sniff file header for encryption. File={File}", path);
            return false;
        }
    }

    /// <summary>
    /// True when the bytes vary the way ciphertext does.
    /// A power cut can leave clusters allocated but never written, which read back as one repeated byte (usually zero), and a text file renamed to .mp4 is nearly all printable characters.
    /// Neither comes from the car's encryption, and calling them encrypted would send the user to change a car setting instead of treating the file as damaged.
    /// </summary>
    private static bool LooksLikeCiphertext(ReadOnlySpan<byte> sample)
    {
        Span<int> counts = stackalloc int[256];
        var mostCommonCount = 0;
        var textCount = 0;

        foreach (var value in sample)
        {
            mostCommonCount = Math.Max(mostCommonCount, ++counts[value]);

            if (value is (>= 0x20 and < 0x7F) or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                textCount++;
            }
        }

        // Ciphertext repeats each byte value about once per 256 bytes, so one value filling half the sample is filler.
        // The wide margin keeps a zero-padded header from making a real encrypted file look like filler.
        var isFiller = mostCommonCount * 2 > sample.Length;

        // Only about 38% of ciphertext bytes happen to be printable, so a sample that is 90% printable is text, even with a byte-order mark or a few non-ASCII characters.
        var isText = textCount * 10 >= sample.Length * 9;

        return !isFiller && !isText;
    }
}
