using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.App.Retrieval;

/// <summary>
/// Retrieval progress seam (ADR-96). The pipeline emits coarse events; the composition root
/// supplies a console implementation (stderr, suppressed by <c>--quiet</c>) or the no-op default.
/// There are no per-detail events.
/// </summary>
public interface IRetrievalProgress
{
    /// <summary>Emitted once before the first list call. <paramref name="total"/> is unknown until the first page.</summary>
    void Started(int? total);

    /// <summary>Emitted after each accepted page.</summary>
    void PageFetched(int pageNumber, int runsInPage);

    /// <summary>Emitted once per new 5% bucket (5, 10, ..., 100). Only when a total is known.</summary>
    void PercentComplete(int percent, int completed, int total);

    /// <summary>Emitted when an invalid/repeated continuation token forces a restart from the top.</summary>
    void Restarting();

    /// <summary>Emitted when the run pauses (ADR-62).</summary>
    void Paused(PauseReason reason, TimeSpan? retryAfter, int? remainingBudget);

    /// <summary>Emitted when retrieval finishes (including an early-stopped refresh).</summary>
    void Completed(int pages, int runsWritten);
}

/// <summary>Default no-op implementation; used when no progress sink is supplied.</summary>
public sealed class NullRetrievalProgress : IRetrievalProgress
{
    public static NullRetrievalProgress Instance { get; } = new();

    private NullRetrievalProgress()
    {
    }

    public void Started(int? total)
    {
    }

    public void PageFetched(int pageNumber, int runsInPage)
    {
    }

    public void PercentComplete(int percent, int completed, int total)
    {
    }

    public void Restarting()
    {
    }

    public void Paused(PauseReason reason, TimeSpan? retryAfter, int? remainingBudget)
    {
    }

    public void Completed(int pages, int runsWritten)
    {
    }
}
