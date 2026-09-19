using System.Globalization;
using BuildAnalytics.App.Retrieval;

namespace BuildAnalytics.App.Cli;

/// <summary>Writes retrieval/report progress to stderr (ADR-82/96/110).</summary>
public sealed class ConsoleRetrievalProgress(IConsoleOutput console) : IRetrievalProgress
{
    private readonly IConsoleOutput _console = console ?? throw new ArgumentNullException(nameof(console));

    public void Started()
    {
    }

    public void PageFetched(int pageNumber, int runsInPage)
        => _console.WriteError($"Retrieving page {pageNumber} - {runsInPage.ToString("N0", CultureInfo.InvariantCulture)} builds");

    public void Restarting()
        => _console.WriteError("Continuation token rejected; restarting from the beginning.");

    public void Completed(int pages, int runs)
    {
    }

    public void GeneratingReport()
        => _console.WriteError("Generating report...");
}
