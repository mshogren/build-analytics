using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Doubles;

public sealed class InMemoryDoublesTests
{
    [Fact]
    public async Task InMemoryRunStore_WriteThenReadRoundTrips()
    {
        var store = new InMemoryRunStore();
        var run = TestRuns.Create(id: 5);

        await store.WriteAsync(run, CancellationToken.None);

        Assert.Equal(run, await store.TryReadAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryRunStore_TryReadAsync_MissingNull()
    {
        var store = new InMemoryRunStore();

        Assert.Null(await store.TryReadAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryRunStore_ListRunIdsAsync_ReturnsWrittenIds()
    {
        var store = new InMemoryRunStore();

        await store.WriteAsync(TestRuns.Create(id: 3), CancellationToken.None);
        await store.WriteAsync(TestRuns.Create(id: 1), CancellationToken.None);

        Assert.Equal([1, 3], await store.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryManifestStore_TryReadAsync_MissingNull()
    {
        var store = new InMemoryManifestStore();

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryTimingReportWriter_CapturesSummary()
    {
        var writer = new InMemoryTimingReportWriter();
        var summary = MonthlyTimingRollup.Summarize([]);

        await writer.WriteAsync(summary, CancellationToken.None);

        Assert.Equal(summary, writer.Summary);
    }
}

internal sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<int, BuildRun> _runs = [];

    public Task WriteAsync(BuildRun run, CancellationToken cancellationToken)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<BuildRun?> TryReadAsync(int runId, CancellationToken cancellationToken)
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

    public Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<int>>(_runs.Keys.OrderBy(id => id).ToArray());
}

internal sealed class InMemoryManifestStore : IManifestStore
{
    public Manifest? Manifest { get; private set; }

    public Task<Manifest?> TryReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Manifest);

    public Task CommitAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        Manifest = manifest;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryTimingReportWriter : ITimingReportWriter
{
    public TimingSummary? Summary { get; private set; }

    public Task WriteAsync(TimingSummary summary, CancellationToken cancellationToken)
    {
        Summary = summary;
        return Task.CompletedTask;
    }
}
