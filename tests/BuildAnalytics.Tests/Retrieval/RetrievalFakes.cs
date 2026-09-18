using BuildAnalytics.App.Retrieval;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Retrieval;

/// <summary>Ordered event sink shared by the retrieval fakes to assert checkpoint ordering.</summary>
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
    private readonly Dictionary<int, BuildRun> _details = [];
    private readonly Dictionary<int, Exception> _detailFailures = [];

    public List<(BuildQuery Query, string? Token)> ListCalls { get; } = [];

    public List<int> DetailCalls { get; } = [];

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

    public FakeBuildSource Detail(int runId, BuildRun run)
    {
        _details[runId] = run;
        return this;
    }

    public FakeBuildSource DetailThrows(int runId, Exception exception)
    {
        _detailFailures[runId] = exception;
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

    public Task<BuildRun> GetDetailAsync(BuildQuery query, int runId, CancellationToken cancellationToken)
    {
        DetailCalls.Add(runId);
        log?.Add($"detail:{runId}");

        if (_detailFailures.TryGetValue(runId, out var failure))
        {
            throw failure;
        }

        return _details.TryGetValue(runId, out var run)
            ? Task.FromResult(run)
            : throw new InvalidOperationException($"No scripted detail for run {runId}.");
    }

    private static string Key(string? token) => token ?? StartKey;
}

internal sealed class RecordingManifestStore(EventLog? log = null) : IManifestStore
{
    public Manifest? Current { get; set; }

    public List<Manifest> Commits { get; } = [];

    public Func<Manifest, Exception?>? OnCommit { get; set; }

    public Task<Manifest?> TryReadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

    public Task CommitAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        var failure = OnCommit?.Invoke(manifest);
        if (failure is not null)
        {
            throw failure;
        }

        Current = manifest;
        Commits.Add(manifest);
        log?.Add($"manifest:{manifest.Status.ToString().ToLowerInvariant()}");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRunStore(EventLog? log = null) : IRunStore
{
    private readonly Dictionary<int, BuildRun> _runs = [];

    public List<BuildRun> Writes { get; } = [];

    public HashSet<int> FailOnWrite { get; } = [];

    public HashSet<int> Unreadable { get; } = [];

    public HashSet<int> StaleSchema { get; } = [];

    public HashSet<int> ListedButAbsent { get; } = [];

    public Dictionary<int, Exception> WriteFailures { get; } = [];

    public void Seed(params int[] runIds)
    {
        foreach (var id in runIds)
        {
            _runs[id] = TestRuns.Create(id: id);
        }
    }

    public void Put(BuildRun run) => _runs[run.Id] = run;

    public Task WriteAsync(BuildRun run, CancellationToken cancellationToken)
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
        Unreadable.Remove(run.Id);
        StaleSchema.Remove(run.Id);
        Writes.Add(run);
        log?.Add($"run:{run.Id}");
        return Task.CompletedTask;
    }

    public Task<BuildRun?> TryReadAsync(int runId, CancellationToken cancellationToken)
    {
        if (Unreadable.Contains(runId))
        {
            throw new CorruptRunFileException(runId);
        }

        if (StaleSchema.Contains(runId))
        {
            throw new UnsupportedSchemaVersionException(BuildRun.CurrentSchemaVersion, BuildRun.CurrentSchemaVersion + 1);
        }

        return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);
    }

    public Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<int>>(
            _runs.Keys.Concat(ListedButAbsent).Distinct().OrderBy(id => id).ToArray());
}

/// <summary>Records the ordered progress events the pipeline emits (ADR-96) and mirrors them into the shared event log.</summary>
internal sealed class RecordingRetrievalProgress(EventLog? log = null) : IRetrievalProgress
{
    public List<string> Events { get; } = [];

    public void Started(int? total) => Record($"started:{total?.ToString() ?? "-"}");

    public void PageFetched(int pageNumber, int runsInPage) => Record($"page:{pageNumber}:{runsInPage}");

    public void PercentComplete(int percent, int completed, int total) => Record($"percent:{percent}:{completed}/{total}");

    public void Restarting() => Record("restarting");

    public void Paused(PauseReason reason, TimeSpan? retryAfter, int? remainingBudget)
        => Record($"paused:{reason}");

    public void Completed(int pages, int runsWritten) => Record($"completed:{pages}:{runsWritten}");

    private void Record(string entry)
    {
        Events.Add(entry);
        log?.Add($"progress:{entry}");
    }
}
