using BuildAnalytics.App.Retrieval;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Retrieval;

/// <summary>Ordered event sink shared by the retrieval fakes to assert append ordering.</summary>
internal sealed class EventLog
{
    public List<string> Events { get; } = [];

    public void Add(string entry) => Events.Add(entry);
}

internal sealed class FakeBuildSource(EventLog? log = null) : IBuildSource
{
    private const string StartKey = "\0start";

    private readonly Dictionary<string, BuildPage> _pages = [];
    private readonly Dictionary<string, Exception> _listFailures = [];

    public List<(BuildQuery Query, string? Token)> ListCalls { get; } = [];

    public Func<BuildQuery, string?, CancellationToken, Task<BuildPage>>? OnList { get; set; }

    public FakeBuildSource Page(string? token, BuildPage page)
    {
        _pages[Key(token)] = page;
        return this;
    }

    public FakeBuildSource ListThrows(string? token, Exception exception)
    {
        _listFailures[Key(token)] = exception;
        return this;
    }

    public Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken cancellationToken)
    {
        ListCalls.Add((query, continuationToken));
        log?.Add($"list:{continuationToken ?? "<start>"}");

        if (OnList is not null)
        {
            return OnList(query, continuationToken, cancellationToken);
        }

        if (_listFailures.TryGetValue(Key(continuationToken), out var failure))
        {
            throw failure;
        }

        return _pages.TryGetValue(Key(continuationToken), out var page)
            ? Task.FromResult(page)
            : throw new InvalidOperationException($"No scripted page for token '{continuationToken}'.");
    }

    private static string Key(string? token) => token ?? StartKey;
}

internal sealed class RecordingRunStore(EventLog? log = null) : IRunStore
{
    private readonly Dictionary<int, BuildRun> _runs = [];

    public List<BuildRun> Writes { get; } = [];

    public HashSet<int> FailOnWrite { get; } = [];

    /// <summary>Ids that exist as a malformed line: excluded from the read result and counted.</summary>
    public HashSet<int> Malformed { get; } = [];

    public Dictionary<int, Exception> WriteFailures { get; } = [];

    public void Seed(params int[] runIds)
    {
        foreach (var id in runIds)
        {
            _runs[id] = TestRuns.Create(id: id);
        }
    }

    public void Put(BuildRun run) => _runs[run.Id] = run;

    public BuildRun? Get(int runId) => _runs.TryGetValue(runId, out var run) ? run : null;

    public Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken)
    {
        var runs = _runs.Values
            .Where(run => !Malformed.Contains(run.Id))
            .OrderBy(run => run.Id)
            .ToArray();
        return Task.FromResult(new RunReadResult(runs, Malformed.Count));
    }

    public Task AppendAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken)
    {
        foreach (var run in runs)
        {
            if (WriteFailures.TryGetValue(run.Id, out var failure))
            {
                throw failure;
            }

            if (FailOnWrite.Contains(run.Id))
            {
                throw new StorageException($"Injected write failure for run {run.Id}.");
            }

            _runs[run.Id] = run;
            Malformed.Remove(run.Id);
            Writes.Add(run);
            log?.Add($"run:{run.Id}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Records the ordered progress events the pipeline emits (ADR-96/110).</summary>
internal sealed class RecordingRetrievalProgress(EventLog? log = null) : IRetrievalProgress
{
    public List<string> Events { get; } = [];

    public void Started() => Record("started");

    public void PageFetched(int pageNumber, int runsInPage) => Record($"page:{pageNumber}:{runsInPage}");

    public void Restarting() => Record("restarting");

    public void Completed(int pages, int runs) => Record($"completed:{pages}:{runs}");

    public void GeneratingReport() => Record("generating-report");

    private void Record(string entry)
    {
        Events.Add(entry);
        log?.Add($"progress:{entry}");
    }
}
