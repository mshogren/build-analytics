using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Doubles;

public sealed class InMemoryDoublesTests
{
    [Fact]
    public async Task InMemoryRunStore_AppendThenReadRoundTrips()
    {
        var store = new InMemoryRunStore();
        var run = TestRuns.Create(id: 5);

        await store.AppendAsync([run], CancellationToken.None);

        Assert.Equal([run], (await store.ReadAllAsync(CancellationToken.None)).Runs);
    }

    [Fact]
    public async Task InMemoryRunStore_ReadAllAsync_AbsentIsEmpty()
    {
        var store = new InMemoryRunStore();

        var result = await store.ReadAllAsync(CancellationToken.None);

        Assert.Empty(result.Runs);
        Assert.Equal(0, result.MalformedLineCount);
    }

    [Fact]
    public async Task InMemoryRunStore_Append_DedupesByLastWriteAndSorts()
    {
        var store = new InMemoryRunStore();

        await store.AppendAsync([TestRuns.Create(id: 3), TestRuns.Create(id: 1)], CancellationToken.None);
        await store.AppendAsync([TestRuns.Create(id: 3, buildNumber: "second")], CancellationToken.None);

        var runs = (await store.ReadAllAsync(CancellationToken.None)).Runs;
        Assert.Equal([1, 3], runs.Select(run => run.Id));
        Assert.Equal("second", runs.Single(run => run.Id == 3).BuildNumber);
    }

    [Fact]
    public async Task InMemoryTimingReportWriter_CapturesReport()
    {
        var writer = new InMemoryTimingReportWriter();
        var summary = MonthlyTimingRollup.Summarize([]);
        var report = new TimingReport(summary, []);

        await writer.WriteAsync(report, CancellationToken.None);

        Assert.Equal(report, writer.Report);
        Assert.Equal(summary, writer.Report!.Summary);
    }
}

internal sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<int, BuildRun> _runs = [];

    public Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken)
        => Task.FromResult(new RunReadResult(_runs.Values.OrderBy(run => run.Id).ToArray(), 0));

    public Task AppendAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken)
    {
        foreach (var run in runs)
        {
            _runs[run.Id] = run;
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryTimingReportWriter : ITimingReportWriter
{
    public TimingReport? Report { get; private set; }

    public Task WriteAsync(TimingReport report, CancellationToken cancellationToken)
    {
        Report = report;
        return Task.CompletedTask;
    }
}
