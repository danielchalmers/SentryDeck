namespace SentryDeck.Tests;

/// <summary>
/// In-memory <see cref="IClipExporter"/> that records requests instead of running FFmpeg.
/// Completes synchronously so controller-backed view-model tests keep their single-thread affinity (see the comments on CreateViewModelWithController).
/// </summary>
internal sealed class FakeClipExporter : IClipExporter
{
    public List<ClipExportRequest> Requests { get; } = [];

    /// <summary>When set, ExportAsync throws this instead of recording a success.</summary>
    public Exception ExceptionToThrow { get; set; }

    /// <summary>
    /// When set, ExportAsync keeps running until it is canceled, like FFmpeg still writing a long export.
    /// It then completes asynchronously, so leave it off in controller-backed tests.
    /// </summary>
    public bool RunsUntilCanceled { get; set; }

    /// <summary>The token the most recent export was given.</summary>
    public CancellationToken LastCancellationToken { get; private set; }

    public Task ExportAsync(ClipExportRequest request, CancellationToken cancellationToken = default)
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        Requests.Add(request);
        LastCancellationToken = cancellationToken;
        return RunsUntilCanceled ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
    }
}
