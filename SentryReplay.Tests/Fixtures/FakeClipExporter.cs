namespace SentryReplay.Tests;

/// <summary>
/// In-memory <see cref="IClipExporter"/> that records requests instead of running FFmpeg.
/// Completes synchronously so controller-backed view-model tests keep their single-thread
/// affinity (see the comments on CreateViewModelWithController).
/// </summary>
internal sealed class FakeClipExporter : IClipExporter
{
    public List<ClipExportRequest> Requests { get; } = [];

    /// <summary>When set, ExportAsync throws this instead of recording a success.</summary>
    public Exception ExceptionToThrow { get; set; }

    public Task ExportAsync(ClipExportRequest request, CancellationToken cancellationToken = default)
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        Requests.Add(request);
        return Task.CompletedTask;
    }
}
