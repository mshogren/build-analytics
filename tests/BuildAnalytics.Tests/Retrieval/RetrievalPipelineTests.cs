using BuildAnalytics.App.Retrieval;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;
using BuildAnalytics.Tests.AzureDevOps;

namespace BuildAnalytics.Tests.Retrieval;

public sealed class RetrievalPipelineTests
{
    // ---- checkpoint ordering / happy path ----

    [Fact]
    public async Task Initial_commit_precedes_the_list_and_completes_after_the_write()
    {
        var (pipeline, source, _, _, manifests, _, log) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.False(result.ShortCircuited);
        Assert.Equal(1, result.PagesFetched);
        Assert.Equal(1, result.RunsWritten);
        Assert.Empty(result.FailedRunIds);
        Assert.Equal(
            ["manifest:inprogress:<null>", "list:<start>", "run:1", "manifest:completed:<null>"],
            log.Events);
        Assert.Equal(ManifestStatus.InProgress, manifests.Commits[0].Status);
        Assert.Null(manifests.Commits[0].Cursor);
    }

    [Fact]
    public async Task Multi_page_forwards_the_cursor_and_checkpoints_each_page()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], "t1"));
        source.Page("t1", new BuildPage([TestRuns.Create(id: 2)], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(2, result.RunsWritten);
        Assert.Equal([null, "t1"], source.ListCalls.Select(call => call.Token));
        Assert.Equal(
            [ManifestStatus.InProgress, ManifestStatus.InProgress, ManifestStatus.Completed],
            manifests.Commits.Select(commit => commit.Status));
        Assert.Equal("t1", manifests.Commits[1].Cursor);
    }

    [Fact]
    public async Task Resume_starts_from_the_manifest_cursor()
    {
        var (pipeline, source, resolver, _, manifests, clock, _) = Create();
        resolver.Ids = [5];
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: "t1", clock, Fingerprint([5]));
        source.Page("t1", new BuildPage([], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(["t1"], source.ListCalls.Select(call => call.Token));
    }

    [Fact]
    public async Task Replay_after_crash_before_commit_upserts_without_duplicates()
    {
        var (pipeline, source, resolver, runs, manifests, clock, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint());
        runs.Seed(1);
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, status: "inProgress")], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(1, result.RunsWritten);
        Assert.Single(runs.Writes);
        Assert.Equal([1], await runs.ListRunIdsAsync(CancellationToken.None));
    }

    // ---- short-circuit / compatibility ----

    [Fact]
    public async Task Completed_manifest_short_circuits_without_resolving_or_listing()
    {
        var (pipeline, source, resolver, _, manifests, clock, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, failedRunIds: [7]);

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.True(result.ShortCircuited);
        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal("c1", result.Cursor);
        Assert.Equal([7], result.FailedRunIds);
        Assert.Empty(resolver.Calls);
        Assert.Empty(source.ListCalls);
        Assert.Empty(manifests.Commits);
    }

    [Fact]
    public async Task Fingerprint_mismatch_on_a_non_empty_root_throws_before_any_commit()
    {
        var (pipeline, source, resolver, runs, manifests, clock, _) = Create();
        resolver.Ids = [5];
        runs.Seed(99);
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: "other");

        await Assert.ThrowsAsync<FingerprintMismatchException>(
            () => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Empty(manifests.Commits);
        Assert.Empty(source.ListCalls);
    }

    [Fact]
    public async Task Resolved_ids_are_stamped_on_the_effective_query_and_patterns_are_forwarded()
    {
        var (pipeline, source, resolver, _, _, _, _) = Create();
        resolver.Ids = [3, 1];
        source.Page(null, new BuildPage([], null));

        await pipeline.RunAsync(Query(names: ["ci-*"]), CancellationToken.None);

        Assert.Equal(["ci-*"], resolver.Calls[0].Patterns);
        Assert.Equal([3, 1], source.ListCalls[0].Query.ResolvedDefinitionIds);
    }

    // ---- detail policy ----

    [Fact]
    public async Task Detail_is_fetched_only_when_the_policy_requires_it()
    {
        var (pipeline, source, _, runs, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));

        await pipeline.RunAsync(Query(DetailPolicy.ListOnly), CancellationToken.None);

        Assert.Empty(source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Detail_replaces_the_list_run()
    {
        var (pipeline, source, _, runs, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, TestRuns.Create(id: 1, definitionName: "from-detail", source: RunSource.Detail));

        await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal([1], source.DetailCalls);
        Assert.Equal(RunSource.Detail, runs.Writes[0].Source);
    }

    [Fact]
    public async Task Detail_404_skips_the_run_records_it_and_still_completes()
    {
        var (pipeline, source, _, runs, _, _, _) = Create();
        source.Page(
            null,
            new BuildPage([TestRuns.Create(id: 1, definitionId: null), TestRuns.Create(id: 2, status: "inProgress")], "t1"));
        source.DetailThrows(1, new RunNotFoundException(1));
        source.Page("t1", new BuildPage([], null));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal([1], result.FailedRunIds);
        Assert.Equal([2], runs.Writes.Select(run => run.Id));
        Assert.Equal([1], source.DetailCalls);
    }

    // ---- failure transitions ----

    [Fact]
    public async Task Run_write_failure_fails_and_never_advances_the_cursor()
    {
        var (pipeline, source, _, runs, manifests, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], "t1"));
        runs.FailOnWrite.Add(1);

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Null(result.Cursor);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
        Assert.Null(manifests.Commits[^1].Cursor);
        Assert.Empty(runs.Writes);
    }

    [Fact]
    public async Task Permanent_ado_failure_fails_the_run()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        source.ListThrows(null, new AdoRequestException(403, "/project/_apis/build/builds", "corr-1"));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
        Assert.Contains("403", manifests.Commits[^1].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paused_exception_persists_paused_and_never_completes()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        source.ListThrows(null, new PipelinePausedException(PauseReason.RunCapReached, remainingBudget: 0));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Paused, result.Status);
        Assert.Null(result.Cursor);
        Assert.Equal(PauseReason.RunCapReached, result.Pause);
        Assert.Equal(0, result.RemainingBudget);
        Assert.Null(result.RetryAfter);
        Assert.Equal(ManifestStatus.Paused, manifests.Commits[^1].Status);
        Assert.NotEqual(ManifestStatus.Completed, manifests.Commits[^1].Status);
        Assert.Contains("RemainingBudget=0", manifests.Commits[^1].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_after_too_long_pauses_with_an_actionable_last_error()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        source.ListThrows(null, new PipelinePausedException(PauseReason.RetryAfterTooLong, retryAfter: TimeSpan.FromSeconds(120)));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Paused, result.Status);
        Assert.Equal(PauseReason.RetryAfterTooLong, result.Pause);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Null(result.RemainingBudget);
        Assert.Contains("retry delay", manifests.Commits[^1].LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RetryAfter=00:02:00", manifests.Commits[^1].LastError, StringComparison.Ordinal);
    }

    // ---- invalid / repeated token ----

    [Fact]
    public async Task Repeated_token_restarts_once_then_fails()
    {
        var (pipeline, source, _, _, _, _, _) = Create();
        source.Page(null, new BuildPage([], "t1"));
        source.Page("t1", new BuildPage([], "t1"));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(["<start>", "t1", "<start>", "t1"], source.ListCalls.Select(call => call.Token ?? "<start>"));
    }

    [Fact]
    public async Task Invalid_token_restarts_once_then_fails()
    {
        var (pipeline, source, _, _, _, _, _) = Create();
        source.ListThrows(null, new InvalidContinuationTokenException(null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(2, source.ListCalls.Count);
    }

    [Fact]
    public async Task Bad_request_without_a_token_fails_without_restarting()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        source.ListThrows(null, new AdoRequestException(400, "/project/_apis/build/builds", null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Single(source.ListCalls);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
    }

    [Fact]
    public async Task Non_404_detail_failure_aborts_failed_without_advancing()
    {
        var (pipeline, source, _, runs, manifests, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], "t1"));
        source.DetailThrows(1, new AdoRequestException(403, "/project/_apis/build/builds/1", "corr-1"));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Null(result.Cursor);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
        Assert.Null(manifests.Commits[^1].Cursor);
        Assert.Empty(runs.Writes);
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancellation_leaves_in_progress_and_makes_no_cleanup_commit()
    {
        var (pipeline, source, _, _, manifests, _, _) = Create();
        using var cts = new CancellationTokenSource();
        source.OnList = (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.RunAsync(Query(), cts.Token));

        Assert.Single(manifests.Commits);
        Assert.Equal(ManifestStatus.InProgress, manifests.Commits[0].Status);
        Assert.Null(manifests.Commits[0].Cursor);
    }

    // ---- helpers ----

    private static BuildQuery Query(DetailPolicy policy = DetailPolicy.ListOnly, IReadOnlyList<string>? names = null)
        => new("https://dev.azure.com/org", "project", null, null, [], policy, "7.1")
        {
            DefinitionNames = names ?? []
        };

    private static string Fingerprint(params int[] ids)
        => BuildQueryFingerprint.Compute(Query() with { ResolvedDefinitionIds = ids });

    private static Manifest ManifestWith(
        ManifestStatus status,
        string? cursor,
        TimeProvider clock,
        string fingerprint = "unused",
        IReadOnlyList<int>? failedRunIds = null)
        => new(
            Manifest.CurrentSchemaVersion,
            fingerprint,
            status,
            cursor,
            clock.GetUtcNow(),
            clock.GetUtcNow(),
            null,
            failedRunIds ?? [],
            [],
            []);

    private static (
        RetrievalPipeline Pipeline,
        FakeBuildSource Source,
        FakeDefinitionResolver Resolver,
        RecordingRunStore Runs,
        RecordingManifestStore Manifests,
        FakeTimeProvider Clock,
        EventLog Log) Create()
    {
        var log = new EventLog();
        var source = new FakeBuildSource(log);
        var resolver = new FakeDefinitionResolver();
        var runs = new RecordingRunStore(log);
        var manifests = new RecordingManifestStore(log);
        var clock = new FakeTimeProvider { UtcNow = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var pipeline = new RetrievalPipeline(source, resolver, runs, manifests, clock);
        return (pipeline, source, resolver, runs, manifests, clock, log);
    }
}
