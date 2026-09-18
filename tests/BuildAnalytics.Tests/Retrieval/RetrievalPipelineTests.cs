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
        var (pipeline, source, _, manifests, _, log, _) = Create();
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
        var (pipeline, source, _, manifests, _, _, _) = Create();
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
        var (pipeline, source, _, manifests, clock, _, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: "t1", clock, Fingerprint());
        source.Page("t1", new BuildPage([], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(["t1"], source.ListCalls.Select(call => call.Token));
    }

    [Fact]
    public async Task Replay_after_crash_before_commit_upserts_without_duplicates()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));
        runs.Put(TestRuns.Create(id: 1, definitionId: null, source: RunSource.List));
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, DetailCompleteRun(1, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(1, result.RunsWritten);
        Assert.Single(runs.Writes);
        Assert.Equal([1], await runs.ListRunIdsAsync(CancellationToken.None));
    }

    // ---- rebuild / replay skip (ADR-7/R11) ----

    [Fact]
    public async Task Rebuild_skips_detail_and_write_for_detail_complete_on_disk_runs()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Put(DetailCompleteRun(1, clock));
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Empty(source.DetailCalls);
        Assert.Empty(runs.Writes);
    }

    [Fact]
    public async Task Rebuild_still_fetches_when_on_disk_file_is_incomplete_under_policy()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Put(TestRuns.Create(id: 1, definitionId: null, source: RunSource.List));
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, DetailCompleteRun(1, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal([1], source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Rebuild_corrupt_on_disk_file_is_treated_as_missing()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(1);
        runs.Unreadable.Add(1);
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, DetailCompleteRun(1, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal([1], source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Rebuild_unsupported_schema_on_disk_run_is_treated_as_missing_and_repaired()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(1);
        runs.StaleSchema.Add(1);
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, DetailCompleteRun(1, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal([1], source.DetailCalls);
        Assert.Single(runs.Writes);
        Assert.Equal(RunSource.Detail, (await runs.TryReadAsync(1, CancellationToken.None))!.Source);
    }

    [Fact]
    public async Task Rebuild_does_not_downgrade_detail_source_to_list()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Put(DetailCompleteRun(1, clock));
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: Fingerprint(DetailPolicy.FillMissing));

        // The list row is contract-complete; without the rebuild skip this would overwrite
        // the detail-sourced file with a thin list row.
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));

        await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Empty(runs.Writes);
        Assert.Equal(RunSource.Detail, (await runs.TryReadAsync(1, CancellationToken.None))!.Source);
    }

    // ---- commit failure / sanitization / cancellation ----

    [Fact]
    public async Task Manifest_commit_failure_propagates()
    {
        var (pipeline, source, _, manifests, _, _, _) = Create();
        manifests.OnCommit = _ => new StorageException("commit failed");

        await Assert.ThrowsAsync<StorageException>(() => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Empty(source.ListCalls);
    }

    [Fact]
    public async Task LastError_contains_no_pat_and_no_absolute_path()
    {
        var (pipeline, source, runs, manifests, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));
        runs.WriteFailures[1] = new StorageException("failed at /home/node/secret/AZDO_PAT/run.json");

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.NotNull(manifests.Commits[^1].LastError);
        Assert.DoesNotContain("/home/node", manifests.Commits[^1].LastError!, StringComparison.Ordinal);
        Assert.DoesNotContain("AZDO_PAT", manifests.Commits[^1].LastError!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ManifestStatus.Paused)]
    [InlineData(ManifestStatus.Failed)]
    public async Task Resume_from_paused_or_failed_uses_the_stored_cursor(ManifestStatus status)
    {
        var (pipeline, source, _, manifests, clock, _, _) = Create();
        manifests.Current = ManifestWith(status, cursor: "t1", clock, fingerprint: Fingerprint());
        source.Page("t1", new BuildPage([], null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Equal(["t1"], source.ListCalls.Select(call => call.Token));
    }

    [Fact]
    public async Task Cancellation_before_the_first_page_makes_no_commit()
    {
        var (pipeline, source, _, manifests, _, _, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.RunAsync(Query(), cts.Token));

        Assert.Empty(manifests.Commits);
        Assert.Empty(source.ListCalls);
    }

    // ---- refresh (ADR-97) ----

    [Fact]
    public async Task Completed_manifest_refreshes_and_early_stops_when_nothing_new()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(1, 2);
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, fingerprint: Fingerprint());
        source.Page(null, new BuildPage([TestRuns.Create(id: 1), TestRuns.Create(id: 2)], null, TotalCount: 2));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.True(result.ShortCircuited);
        Assert.Equal(1, result.PagesFetched);
        Assert.Equal(0, result.RunsWritten);
        Assert.Empty(result.FailedRunIds);

        // The refresh re-lists from the top, not the stored cursor, and stops after one call.
        Assert.Equal([null], source.ListCalls.Select(call => call.Token));
        Assert.Equal([ManifestStatus.InProgress, ManifestStatus.Completed], manifests.Commits.Select(commit => commit.Status));
    }

    [Fact]
    public async Task Completed_manifest_refresh_does_not_early_stop_before_retrying_a_failed_run()
    {
        // A prior failure on a later page must be re-encountered before the early stop, even
        // when the newest page is fully stored (reviewer repro).
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(1);
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, fingerprint: Fingerprint(), failedRunIds: [7]);
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], "next", TotalCount: 2));
        source.Page("next", new BuildPage([TestRuns.Create(id: 7)], null, TotalCount: 2));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.False(result.ShortCircuited);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(1, result.RunsWritten);
        Assert.Empty(result.FailedRunIds);
        Assert.Equal([7], runs.Writes.Select(run => run.Id));
    }

    [Fact]
    public async Task Completed_manifest_refresh_preserves_a_failed_id_that_is_no_longer_listed()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(1);
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, fingerprint: Fingerprint(), failedRunIds: [7]);
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null, TotalCount: 1));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.True(result.ShortCircuited);
        Assert.Equal([7], result.FailedRunIds);
        Assert.Equal([7], manifests.Commits[^1].FailedRunIds);
    }

    [Fact]
    public async Task Completed_manifest_refresh_writes_new_runs_and_preserves_created_at()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        var created = new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero);
        runs.Seed(1);
        manifests.Current = new Manifest(
            Manifest.CurrentSchemaVersion,
            Fingerprint(),
            ManifestStatus.Completed,
            "c1",
            created,
            clock.GetUtcNow(),
            null,
            [],
            [],
            []);
        source.Page(null, new BuildPage([TestRuns.Create(id: 1), TestRuns.Create(id: 2)], null, TotalCount: 2));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.False(result.ShortCircuited);
        Assert.Equal(1, result.RunsWritten);
        Assert.Equal([2], runs.Writes.Select(run => run.Id));
        Assert.Equal(created, manifests.Commits[^1].CreatedAt);
    }

    [Fact]
    public async Task Completed_manifest_refresh_retries_previously_failed_runs()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, fingerprint: Fingerprint(DetailPolicy.FillMissing), failedRunIds: [7]);
        source.Page(null, new BuildPage([TestRuns.Create(id: 7, definitionId: null)], null, TotalCount: 1));
        source.Detail(7, DetailCompleteRun(7, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Completed, result.Status);
        Assert.Empty(result.FailedRunIds);
        Assert.Equal(1, result.RunsWritten);
        Assert.Equal([7], source.DetailCalls);
        Assert.Equal([7], runs.Writes.Select(run => run.Id));
    }

    [Fact]
    public async Task Completed_manifest_with_a_different_fingerprint_is_a_typed_error_even_when_empty()
    {
        var (pipeline, source, _, manifests, clock, _, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed, cursor: "c1", clock, fingerprint: "other");

        await Assert.ThrowsAsync<FingerprintMismatchException>(() => pipeline.RunAsync(Query(), CancellationToken.None));
        Assert.Empty(source.ListCalls);
    }

    [Fact]
    public async Task Fingerprint_mismatch_on_a_non_empty_root_throws_before_any_commit()
    {
        var (pipeline, source, runs, manifests, clock, _, _) = Create();
        runs.Seed(99);
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: null, clock, fingerprint: "other");

        await Assert.ThrowsAsync<FingerprintMismatchException>(
            () => pipeline.RunAsync(Query(), CancellationToken.None));

        Assert.Empty(manifests.Commits);
        Assert.Empty(source.ListCalls);
    }

    // ---- detail policy ----

    [Fact]
    public async Task Detail_is_fetched_only_when_the_policy_requires_it()
    {
        var (pipeline, source, runs, _, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));

        await pipeline.RunAsync(Query(DetailPolicy.ListOnly), CancellationToken.None);

        Assert.Empty(source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Detail_replaces_the_list_run()
    {
        var (pipeline, source, runs, _, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], null));
        source.Detail(1, TestRuns.Create(id: 1, definitionName: "from-detail", source: RunSource.Detail));

        await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal([1], source.DetailCalls);
        Assert.Equal(RunSource.Detail, runs.Writes[0].Source);
    }

    [Fact]
    public async Task Detail_404_skips_the_run_records_it_and_still_completes()
    {
        var (pipeline, source, runs, _, _, _, _) = Create();
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
        var (pipeline, source, runs, manifests, _, _, _) = Create();
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
        var (pipeline, source, _, manifests, _, _, _) = Create();
        source.ListThrows(null, new AdoRequestException(403, "/project/_apis/build/builds", "corr-1"));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
        Assert.Contains("403", manifests.Commits[^1].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paused_exception_persists_paused_and_never_completes()
    {
        var (pipeline, source, _, manifests, _, _, progress) = Create();
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
        Assert.Contains("paused:RunCapReached", progress.Events);
    }

    [Fact]
    public async Task Retry_after_too_long_pauses_with_an_actionable_last_error()
    {
        var (pipeline, source, _, manifests, _, _, _) = Create();
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
        var (pipeline, source, _, _, _, _, progress) = Create();
        source.Page(null, new BuildPage([], "t1"));
        source.Page("t1", new BuildPage([], "t1"));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(["<start>", "t1", "<start>", "t1"], source.ListCalls.Select(call => call.Token ?? "<start>"));
        Assert.Equal(1, progress.Events.Count(entry => entry == "restarting"));
    }

    [Fact]
    public async Task Invalid_token_restarts_once_then_fails()
    {
        var (pipeline, source, _, _, _, _, progress) = Create();
        source.ListThrows(null, new InvalidContinuationTokenException(null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(2, source.ListCalls.Count);
        Assert.Equal(1, progress.Events.Count(entry => entry == "restarting"));
    }

    [Fact]
    public async Task Bad_request_without_a_token_fails_without_restarting()
    {
        var (pipeline, source, _, manifests, _, _, _) = Create();
        source.ListThrows(null, new AdoRequestException(400, "/project/_apis/build/builds", null));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Single(source.ListCalls);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
    }

    [Fact]
    public async Task Non_404_detail_failure_aborts_failed_without_advancing()
    {
        var (pipeline, source, runs, manifests, _, _, _) = Create();
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
        var (pipeline, source, _, manifests, _, _, _) = Create();
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

    // ---- ADR-84..93 correctness ----

    [Fact]
    public async Task Restart_within_a_pass_does_not_refetch_or_rewrite_already_written_runs()
    {
        var (pipeline, source, runs, _, clock, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], "t1"));
        source.Page("t1", new BuildPage([], "t1"));
        source.Detail(1, DetailCompleteRun(1, clock));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(1, result.RunsWritten);
        Assert.Single(source.DetailCalls);
        Assert.Single(runs.Writes);
    }

    [Fact]
    public async Task Token_cycle_with_period_greater_than_one_restarts_once_then_fails()
    {
        var (pipeline, source, _, _, _, _, _) = Create();
        source.Page(null, new BuildPage([], "t1"));
        source.Page("t1", new BuildPage([], "t2"));
        source.Page("t2", new BuildPage([], "t1"));

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(
            ["<start>", "t1", "t2", "<start>", "t1", "t2"],
            source.ListCalls.Select(call => call.Token ?? "<start>"));
    }

    [Fact]
    public async Task Invalid_detail_payload_aborts_failed_and_is_not_a_per_run_skip()
    {
        var (pipeline, source, runs, manifests, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null)], "t1"));
        source.DetailThrows(1, new InvalidDetailPayloadException(1, 2));

        var result = await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Empty(runs.Writes);
        Assert.Empty(result.FailedRunIds);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
    }

    [Fact]
    public async Task Unexpected_exception_fails_the_run_and_persists_failed()
    {
        var (pipeline, source, runs, manifests, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1)], null));
        runs.WriteFailures[1] = new InvalidOperationException("boom");

        var result = await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(ManifestStatus.Failed, result.Status);
        Assert.Equal(ManifestStatus.Failed, manifests.Commits[^1].Status);
        Assert.Contains("InvalidOperationException", manifests.Commits[^1].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkpoint_commit_carries_failed_run_ids_with_the_cursor()
    {
        var (pipeline, source, _, manifests, _, _, _) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1, definitionId: null), TestRuns.Create(id: 2, status: "inProgress")], "t1"));
        source.DetailThrows(1, new RunNotFoundException(1));
        source.Page("t1", new BuildPage([], null));

        await pipeline.RunAsync(Query(DetailPolicy.FillMissing), CancellationToken.None);

        var checkpoint = manifests.Commits.Single(commit => commit.Cursor == "t1");
        Assert.Equal([1], checkpoint.FailedRunIds);
    }

    // ---- progress (ADR-96) ----

    [Fact]
    public async Task Progress_emits_page_lines_and_5_percent_buckets()
    {
        var (pipeline, source, _, _, _, _, progress) = Create();
        source.Page(null, new BuildPage(Enumerable.Range(1, 15).Select(id => TestRuns.Create(id: id)).ToArray(), null, TotalCount: 100));

        await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(
            [
                "started:-",
                "page:1:15",
                "percent:5:15/100",
                "percent:10:15/100",
                "percent:15:15/100",
                "completed:1:15"
            ],
            progress.Events);
    }

    [Fact]
    public async Task Progress_percentage_continues_from_the_on_disk_baseline()
    {
        var (pipeline, source, runs, manifests, clock, _, progress) = Create();
        runs.Seed(Enumerable.Range(1, 80).ToArray());
        manifests.Current = ManifestWith(ManifestStatus.InProgress, cursor: "t1", clock, fingerprint: Fingerprint());
        source.Page("t1", new BuildPage(Enumerable.Range(81, 15).Select(id => TestRuns.Create(id: id)).ToArray(), null, TotalCount: 95));

        await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Contains("percent:85:95/95", progress.Events);
        Assert.Contains("percent:90:95/95", progress.Events);
        Assert.Contains("percent:95:95/95", progress.Events);
    }

    [Fact]
    public async Task Progress_without_a_total_emits_page_lines_only()
    {
        var (pipeline, source, _, _, _, _, progress) = Create();
        source.Page(null, new BuildPage([TestRuns.Create(id: 1), TestRuns.Create(id: 2)], null));

        await pipeline.RunAsync(Query(), CancellationToken.None);

        Assert.Equal(["started:-", "page:1:2", "completed:1:2"], progress.Events);
    }

    // ---- helpers ----

    private static BuildRun DetailCompleteRun(int id, TimeProvider clock)
        => TestRuns.Create(
            id: id,
            queueTime: clock.GetUtcNow(),
            startTime: clock.GetUtcNow(),
            finishTime: clock.GetUtcNow(),
            source: RunSource.Detail);

    private static BuildQuery Query(DetailPolicy policy = DetailPolicy.ListOnly)
        => new("https://dev.azure.com/org", "project", policy, "7.1");

    private static string Fingerprint(DetailPolicy policy = DetailPolicy.ListOnly)
        => BuildQueryFingerprint.Compute(Query(policy));

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
        RecordingRunStore Runs,
        RecordingManifestStore Manifests,
        FakeTimeProvider Clock,
        EventLog Log,
        RecordingRetrievalProgress Progress) Create()
    {
        var log = new EventLog();
        var source = new FakeBuildSource(log);
        var runs = new RecordingRunStore(log);
        var manifests = new RecordingManifestStore(log);
        var clock = new FakeTimeProvider { UtcNow = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var progress = new RecordingRetrievalProgress();
        var pipeline = new RetrievalPipeline(source, runs, manifests, clock, progress);
        return (pipeline, source, runs, manifests, clock, log, progress);
    }
}
