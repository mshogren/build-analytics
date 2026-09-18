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

internal sealed class FakeDefinitionResolver : IDefinitionResolver
{
    public IReadOnlyList<int> Ids { get; set; } = [];

    public List<(BuildQuery Query, IReadOnlyList<string> Patterns)> Calls { get; } = [];

    public Task<IReadOnlyList<int>> ResolveAsync(BuildQuery query, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        Calls.Add((query, patterns.ToArray()));
        return Task.FromResult(Ids);
    }
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
        log?.Add($"manifest:{manifest.Status.ToString().ToLowerInvariant()}:{manifest.Cursor ?? "<null>"}");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRunStore(EventLog? log = null) : IRunStore
{
    private readonly Dictionary<int, BuildRun> _runs = [];

    public List<BuildRun> Writes { get; } = [];

    public HashSet<int> FailOnWrite { get; } = [];

    public void Seed(params int[] runIds)
    {
        foreach (var id in runIds)
        {
            _runs[id] = TestRuns.Create(id: id);
        }
    }

    public Task WriteAsync(BuildRun run, CancellationToken cancellationToken)
    {
        if (FailOnWrite.Contains(run.Id))
        {
            throw new StorageException($"Injected write failure for run {run.Id}.");
        }

        _runs[run.Id] = run;
        Writes.Add(run);
        log?.Add($"run:{run.Id}");
        return Task.CompletedTask;
    }

    public Task<BuildRun?> TryReadAsync(int runId, CancellationToken cancellationToken)
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

    public Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<int>>(_runs.Keys.OrderBy(id => id).ToArray());
}
