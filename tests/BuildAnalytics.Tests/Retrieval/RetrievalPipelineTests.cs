using BuildAnalytics.App.Retrieval;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Retrieval;

public sealed class RetrievalPipelineTests
{
    // ---- happy path / paging ----

    [Fact]
    public async Task Lists_every_page_from_the_beginning_and_appends_each()
    {
        var (pipeline, source, runs, log, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], "t1"));
        source.Page("t1", new BuildPage([TestRuns.Create(id: 2)], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(2, result.RunsWritten);
        Assert.Empty(result.FailedRunIds);
        Assert.Equal([null, "t1"], source.ListCalls.Select(call => call.Token));
        Assert.Equal([1, 2], runs.Writes.Select(run => run.Id));
        Assert.Equal(
            ["progress:started", "list:<start>", "run:1", "progress:page:1:1", "list:t1", "run:2", "progress:page:2:1", "progress:completed:2:2"],
            log.Events);
    }

    [Fact]
    public async Task Page_is_not_announced_when_the_append_fails()
    {
        var (pipeline, source, runs, _, progress) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));
        runs.FailOnWrite.Add(1);

        await Assert.ThrowsAsync<StorageException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        // FIX1: the page line is emitted only after the durable append succeeds.
        Assert.DoesNotContain("page:1:1", progress.Events);
    }

    [Fact]
    public async Task Lists_a_three_page_continuation_chain()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], "t1"));
        source.Page("t1", new BuildPage([TestRuns.Create(id: 2)], "t2"));
        source.Page("t2", new BuildPage([TestRuns.Create(id: 3)], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(3, result.PagesFetched);
        Assert.Equal(3, result.RunsWritten);
        Assert.Equal(3, source.ListCalls.Count);
        Assert.Equal([null, "t1", "t2"], source.ListCalls.Select(call => call.Token));
        Assert.Equal([1, 2, 3], runs.Writes.Select(run => run.Id));
    }

    [Fact]
    public async Task Duplicate_ids_within_a_pass_are_appended_once()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, buildNumber: "first"), TestRuns.Create(id: 1, buildNumber: "second")], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(1, result.RunsWritten);
        Assert.Equal([1], runs.Writes.Select(run => run.Id));
        Assert.Equal("first", runs.Get(1)!.BuildNumber);
    }

    // ---- detail policy ----

    [Fact]
    public async Task Detail_is_fetched_only_when_the_policy_requires_it()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));

        await pipeline.RunAsync(Query(DetailPolicy.ListOnly), CancellationToken.None);

        Assert.Empty(source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Detail_replaces_the_list_run()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, TestRuns.Create(id: 1, definitionName: "from-detail", source: RunSource.Detail));

        await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal([1], source.DetailCalls);
        Assert.Equal(RunSource.Detail, runs.Writes[0].Source);
    }

    [Fact]
    public async Task Detail_404_skips_the_run_records_it_and_still_completes()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage(
            [TestRuns.Create(id: 1, definitionId: null), TestRuns.Create(id: 2, status: "inProgress")],
            "t1"));
        source.DetailThrows(1, new RunNotFoundException(1));
        source.Page("t1", new BuildPage([], null));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal([1], result.FailedRunIds);
        Assert.Equal([2], runs.Writes.Select(run => run.Id));
        Assert.Equal([1], source.DetailCalls);
    }

    // ---- failure transitions ----

    [Fact]
    public async Task Permanent_ado_failure_propagates()
    {
        var (pipeline, source, _, _, _) = Create();
        source.ListThrows(null, new AdoRequestException(403, "/project/_apis/build/builds", "corr-1"));

        await Assert.ThrowsAsync<AdoRequestException>(() => pipeline.RunAsync(Query(), CancellationToken.None));
    }

    [Fact]
    public async Task Paused_exception_propagates()
    {
        var (pipeline, source, _, _, _) = Create();
        source.ListThrows(null, new PipelinePausedException(PauseReason.RunCapReached, remainingBudget: 0));

        var exception = await Assert.ThrowsAsync<PipelinePausedException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Equal(PauseReason.RunCapReached, exception.Reason);
        Assert.Empty(source.DetailCalls);
    }

    [Fact]
    public async Task Unexpected_exception_propagates()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));
        runs.WriteFailures[1] = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(Query(), CancellationToken.None));
    }

    // ---- invalid / repeated token ----

    [Fact]
    public async Task Repeated_token_restarts_once_from_null_then_fails()
    {
        var (pipeline, source, _, _, progress) = Create();
        source.Page(null, new BuildPage([], "t1"));
        source.Page("t1", new BuildPage([], "t1"));

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Equal(["<start>", "t1", "<start>", "t1"], source.ListCalls.Select(call => call.Token ?? "<start>"));
        Assert.Equal(1, progress.Events.Count(entry => entry == "restarting"));
    }

    [Fact]
    public async Task Invalid_token_restarts_once_from_null_then_fails()
    {
        var (pipeline, source, _, _, progress) = Create();
        source.ListThrows(null, new InvalidContinuationTokenException(null));

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Equal(2, source.ListCalls.Count);
        Assert.Equal(1, progress.Events.Count(entry => entry == "restarting"));
    }

    [Fact]
    public async Task Token_cycle_with_period_greater_than_one_restarts_once_then_fails()
    {
        var (pipeline, source, _, _, _) = Create();
        source.Page(null, new BuildPage([], "t1"));
        source.Page("t1", new BuildPage([], "t2"));
        source.Page("t2", new BuildPage([], "t1"));

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Equal(
            ["<start>", "t1", "t2", "<start>", "t1", "t2"],
            source.ListCalls.Select(call => call.Token ?? "<start>"));
    }

    [Fact]
    public async Task Restart_replay_does_not_reappend_or_refetch()
    {
        var (pipeline, source, runs, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], "t1"));
        source.Page("t1", new BuildPage([], "t1"));
        source.Detail(1, TestRuns.Create(id: 1, source: RunSource.Detail));

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None));

        Assert.Equal([1], source.DetailCalls);
        Assert.Equal([1], runs.Writes.Select(run => run.Id));
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancellation_propagates_with_no_further_appends()
    {
        var (pipeline, source, runs, _, _) = Create();
        using var cts = new CancellationTokenSource();
        source.OnList = (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.RunAsync(Query(), cts.Token));

        Assert.Empty(runs.Writes);
    }

    // ---- progress ----

    [Fact]
    public async Task Progress_emits_started_page_and_completed_events()
    {
        var (pipeline, source, _, _, progress) = Create();
        source.Page(null, new BuildPage(Enumerable.Range(1, 15).Select(id => TestRuns.Create(id: id)).ToArray(), null, TotalCount: 100));

        await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(["started", "page:1:15", "completed:1:15"], progress.Events);
        Assert.DoesNotContain(progress.Events, entry => entry.StartsWith("percent", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static BuildQuery Query(DetailPolicy policy = DetailPolicy.ListOnly)
        => new("https://dev.azure.com/org", "project", policy, "7.1");

    private static (
        RetrievalPipeline Pipeline,
        FakeBuildSource Source,
        RecordingRunStore Runs,
        EventLog Log,
        RecordingRetrievalProgress Progress) Create()
    {
        var log = new EventLog();
        var source = new FakeBuildSource(log);
        var runs = new RecordingRunStore(log);
        var progress = new RecordingRetrievalProgress(log);
        var pipeline = new RetrievalPipeline(source, runs, progress);
        return (pipeline, source, runs, log, progress);
    }
}
