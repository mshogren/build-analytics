using BuildAnalytics.App.Retrieval;
using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Writes coarse retrieval progress to stderr (ADR-96/ADR-82). No per-detail lines.
/// </summary>
public sealed class ConsoleRetrievalProgress(IConsoleOutput console) : IRetrievalProgress
{
    private readonly IConsoleOutput _console = console ?? throw new ArgumentNullException(nameof(console));

    public void Started(int? total)
        => _console.WriteError(total is { } value ? $"Retrieving {value} run(s)..." : "Retrieving...");

    public void PageFetched(int pageNumber, int runsInPage)
        => _console.WriteError($"  page {pageNumber}: {runsInPage} run(s)");

    public void PercentComplete(int percent, int completed, int total)
        => _console.WriteError($"  {percent}% ({completed}/{total})");

    public void Restarting()
        => _console.WriteError("  continuation token rejected; restarting from the beginning.");

    public void Paused(PauseReason reason, TimeSpan? retryAfter, int? remainingBudget)
    {
        var details = new List<string> { $"reason={reason}" };
        if (retryAfter is { } delay)
        {
            details.Add($"retryAfter={delay}");
        }

        if (remainingBudget is { } budget)
        {
            details.Add($"remainingBudget={budget}");
        }

        _console.WriteError($"Retrieval paused ({string.Join(", ", details)}).");
    }

    public void Completed(int pages, int runsWritten)
        => _console.WriteError($"Retrieval completed: {runsWritten} run(s), {pages} page(s).");
}
