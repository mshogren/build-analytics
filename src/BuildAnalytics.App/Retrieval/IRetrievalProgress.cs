namespace BuildAnalytics.App.Retrieval;

/// <summary>
/// Retrieval/report progress seam (ADR-96/110). The composition root supplies a console
/// implementation (stderr, suppressed by <c>--quiet</c>) or the no-op default.
/// </summary>
public interface IRetrievalProgress
{
    /// <summary>Emitted once before the first list call.</summary>
    void Started();

    /// <summary>Emitted after each page is listed and appended.</summary>
    void PageFetched(int pageNumber, int runsInPage);

    /// <summary>Emitted when an invalid/repeated continuation token forces a restart from the top.</summary>
    void Restarting();

    /// <summary>Emitted once retrieval finishes.</summary>
    void Completed(int pages, int runs);

    /// <summary>Emitted before the report is generated (phase transition).</summary>
    void GeneratingReport();
}

/// <summary>Default no-op implementation; used when <c>--quiet</c> is set or no sink is supplied.</summary>
public sealed class NullRetrievalProgress : IRetrievalProgress
{
    public static NullRetrievalProgress Instance { get; } = new();

    private NullRetrievalProgress()
    {
    }

    public void Started()
    {
    }

    public void PageFetched(int pageNumber, int runsInPage)
    {
    }

    public void Restarting()
    {
    }

    public void Completed(int pages, int runs)
    {
    }

    public void GeneratingReport()
    {
    }
}
