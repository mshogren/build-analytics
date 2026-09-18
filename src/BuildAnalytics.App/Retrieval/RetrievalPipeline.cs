using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Retrieval;

/// <summary>Outcome of one retrieval run. Non-completed statuses are resumable (ADR-67/70).</summary>
public sealed record RetrievalResult(
    ManifestStatus Status,
    bool ShortCircuited,
    int PagesFetched,
    int RunsWritten,
    IReadOnlyList<int> FailedRunIds,
    PauseReason? Pause,
    TimeSpan? RetryAfter,
    int? RemainingBudget);

/// <summary>
/// List -> detail-if-needed -> durable run write -> manifest checkpoint (ADR-4/66..99).
/// Owns paging, token-cycle detection, refresh early-stop, and terminal status; the adapter
/// owns the run budget and HTTP retry.
/// </summary>
public sealed class RetrievalPipeline(
    IBuildSource source,
    IRunStore runs,
    IManifestStore manifests,
    TimeProvider clock,
    IRetrievalProgress? progress = null)
{
    private readonly IRetrievalProgress _progress = progress ?? NullRetrievalProgress.Instance;

    public async Task<RetrievalResult> RunAsync(BuildQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await manifests.TryReadAsync(cancellationToken).ConfigureAwait(false);
        var isCompleted = existing is { Status: ManifestStatus.Completed };

        // ADR-88/92: the fingerprint is checked BEFORE any list call; a different query on a
        // (completed or non-empty) root is a typed error, not a silent no-op.
        var fingerprint = BuildQueryFingerprint.Compute(query);

        var existingRunIds = await runs.ListRunIdsAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ManifestCompatibility.EnsureCompatible(existing, fingerprint, existingRunIds, requireMatch: isCompleted);
        }

        var createdAt = existing?.CreatedAt ?? clock.GetUtcNow();

        // ADR-97: keep the previously failed ids (they must be retried, and an id that is no
        // longer listed must never be silently dropped). pendingFailed gates the refresh early
        // stop until every prior failure has been re-encountered.
        var failedRunIds = new List<int>(existing?.FailedRunIds ?? []);
        var pendingFailed = new HashSet<int>(failedRunIds);
        var existingIdSet = new HashSet<int>(existingRunIds);
        var writtenThisPass = new HashSet<int>();
        var baseline = existingRunIds.Count;

        // ADR-108: there is no resume cursor. Resume and refresh are one path - every pass
        // lists from the start (continuationToken starts null) and skips runs already durable
        // on disk (ADR-78). The transient continuation token only advances paging within this
        // pass; it is never persisted or restored.
        string? continuationToken = null;

        // ADR-97: only a completed refresh may stop at the first page that adds no new runs.
        var canEarlyStop = isCompleted;

        var pagesFetched = 0;
        var runsWritten = 0;
        var total = (int?)null;
        var handled = 0;
        var emittedBucket = 0;

        void ReportPercent()
        {
            if (total is not { } totalCount || totalCount <= 0)
            {
                return;
            }

            var completed = Math.Min(baseline + handled, totalCount);
            var percent = completed * 100 / totalCount;
            while (emittedBucket + 5 <= percent)
            {
                emittedBucket += 5;
                _progress.PercentComplete(emittedBucket, completed, totalCount);
            }
        }

        async Task<RetrievalResult> PauseAsync(PipelinePausedException exception)
        {
            // ADR-62: paused is resumable and never completed; the reason and typed
            // RetryAfter/RemainingBudget are surfaced structurally and in lastError.
            await CommitAsync(ManifestStatus.Paused, DescribePause(exception)).ConfigureAwait(false);
            _progress.Paused(exception.Reason, exception.RetryAfter, exception.RemainingBudget);
            return Result(ManifestStatus.Paused, pagesFetched, runsWritten, failedRunIds, exception.Reason, exception.RetryAfter, exception.RemainingBudget);
        }

        async Task CommitAsync(ManifestStatus status, string? lastError)
        {
            var manifest = new Manifest(
                Manifest.CurrentSchemaVersion,
                fingerprint,
                status,
                createdAt,
                clock.GetUtcNow(),
                lastError,
                failedRunIds);

            await manifests.CommitAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        async Task<RetrievalResult> CompleteAsync()
        {
            // ADR-67: exhausted or early-stopped with failures is still completed.
            await CommitAsync(ManifestStatus.Completed, lastError: null).ConfigureAwait(false);
            _progress.Completed(pagesFetched, runsWritten);
            var result = Result(ManifestStatus.Completed, pagesFetched, runsWritten, failedRunIds);

            // ADR-97: only a refresh that wrote nothing is marked short-circuited.
            return result with { ShortCircuited = isCompleted && runsWritten == 0 };
        }

        try
        {
            // ADR-68: a fingerprinted, resumable root exists before the first list call.
            await CommitAsync(ManifestStatus.InProgress, lastError: null).ConfigureAwait(false);
            _progress.Started(total);

            var restarted = false;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BuildPage page;
                try
                {
                    page = await source.ListAsync(query, continuationToken, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidContinuationTokenException exception)
                {
                    if (restarted)
                    {
                        await CommitAsync(ManifestStatus.Failed, exception.Message).ConfigureAwait(false);
                        return Result(ManifestStatus.Failed, pagesFetched, runsWritten, failedRunIds);
                    }

                    // ADR-85/108: restart the pass once from the beginning (null), then fail.
                    restarted = true;
                    seenTokens.Clear();
                    continuationToken = null;
                    _progress.Restarting();
                    await CommitAsync(ManifestStatus.InProgress, lastError: null).ConfigureAwait(false);
                    continue;
                }

                pagesFetched++;
                total ??= page.TotalCount;
                _progress.PageFetched(pagesFetched, page.Runs.Count);

                // ADR-104: reflect the on-disk baseline at page start; per-run ticks follow below.
                ReportPercent();

                var runsBeforePage = runsWritten;
                var pageRecordedFailure = false;

                foreach (var listed in page.Runs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // ADR-97: this prior failure has now been re-encountered (retried below).
                    pendingFailed.Remove(listed.Id);

                    // ADR-89: a run written earlier in this pass is skipped with no re-read/re-fetch/rewrite.
                    if (writtenThisPass.Contains(listed.Id))
                    {
                        continue;
                    }

                    // ADR-7/R11: on-disk files the active policy already accepts are left untouched
                    // (never downgrade detail -> list). Corrupt/stale/unreadable files are re-fetched.
                    if (existingIdSet.Contains(listed.Id))
                    {
                        var onDisk = await TryReadExistingRunAsync(listed.Id, cancellationToken).ConfigureAwait(false);
                        if (onDisk is not null && !DetailPolicyEvaluator.NeedsDetail(onDisk, query.DetailPolicy))
                        {
                            // A run that is now on disk is no longer a failure.
                            failedRunIds.Remove(listed.Id);
                            continue;
                        }
                    }

                    var run = listed;
                    if (DetailPolicyEvaluator.NeedsDetail(run, query.DetailPolicy))
                    {
                        try
                        {
                            run = await source.GetDetailAsync(query, run.Id, cancellationToken).ConfigureAwait(false);
                        }
                        catch (RunNotFoundException)
                        {
                            // ADR-67: a missing run is skipped, recorded, and does not block the page.
                            if (!failedRunIds.Contains(run.Id))
                            {
                                failedRunIds.Add(run.Id);
                            }

                            // ADR-102: only ids outside the on-disk baseline advance the percentage.
                            if (!existingIdSet.Contains(run.Id))
                            {
                                handled++;
                                ReportPercent();
                            }

                            pageRecordedFailure = true;
                            continue;
                        }
                    }

                    await runs.WriteAsync(run, cancellationToken).ConfigureAwait(false);
                    runsWritten++;
                    if (!existingIdSet.Contains(run.Id))
                    {
                        handled++;
                        ReportPercent();
                    }

                    writtenThisPass.Add(run.Id);
                    failedRunIds.Remove(run.Id);
                }

                // ADR-104: with a descending list, a page that adds no new runs means every
                // older page is already stored -> stop paging, but only for a completed-root
                // refresh, only once the page recorded no failure, and only after every prior
                // failure has been re-encountered (a failed run is not "already stored").
                var pageAddedNewRuns = runsWritten > runsBeforePage;
                if (canEarlyStop && page.Runs.Count > 0 && !pageAddedNewRuns && !pageRecordedFailure && pendingFailed.Count == 0)
                {
                    return await CompleteAsync().ConfigureAwait(false);
                }

                var next = page.ContinuationToken;
                if (next is not null && !seenTokens.Add(next))
                {
                    // ADR-85: any revisited token (including a >1 page cycle) is a token failure.
                    if (restarted)
                    {
                        await CommitAsync(ManifestStatus.Failed, "Azure DevOps returned a repeated continuation token.").ConfigureAwait(false);
                        return Result(ManifestStatus.Failed, pagesFetched, runsWritten, failedRunIds);
                    }

                    // ADR-85/108: restart the pass once from the beginning (null), then fail.
                    restarted = true;
                    seenTokens.Clear();
                    continuationToken = null;
                    _progress.Restarting();
                    await CommitAsync(ManifestStatus.InProgress, lastError: null).ConfigureAwait(false);
                    continue;
                }

                if (next is null)
                {
                    return await CompleteAsync().ConfigureAwait(false);
                }

                continuationToken = next;
                await CommitAsync(ManifestStatus.InProgress, lastError: null).ConfigureAwait(false);
            }
        }
        catch (PipelinePausedException exception)
        {
            return await PauseAsync(exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ADR-70: leave the last committed status; no advance, no cleanup commit.
            throw;
        }
        catch (Exception exception)
        {
            // ADR-84/91: any other failure aborts Failed with a best-effort sanitized commit.
            await CommitAsync(ManifestStatus.Failed, DescribeError(exception)).ConfigureAwait(false);
            return Result(ManifestStatus.Failed, pagesFetched, runsWritten, failedRunIds);
        }
    }

    /// <summary>
    /// ADR-7/R11/ADR-83: a corrupt, unreadable, or stale-schema existing run is treated as
    /// missing and re-fetched/rewritten - run files are a repairable cache. The manifest's
    /// schema mismatch stays fatal (ADR-8) because the manifest is authoritative state, and
    /// reporting still aborts on a stale run schema (ADR-77) since it cannot repair offline.
    /// </summary>
    private async Task<BuildRun?> TryReadExistingRunAsync(int runId, CancellationToken cancellationToken)
    {
        try
        {
            return await runs.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (CorruptRunFileException)
        {
            return null;
        }
        catch (UnsupportedSchemaVersionException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static RetrievalResult Result(
        ManifestStatus status,
        int pagesFetched,
        int runsWritten,
        IReadOnlyList<int> failedRunIds,
        PauseReason? pause = null,
        TimeSpan? retryAfter = null,
        int? remainingBudget = null)
        => new(status, ShortCircuited: false, pagesFetched, runsWritten, failedRunIds.ToArray(), pause, retryAfter, remainingBudget);

    /// <summary>ADR-27: typed ADO messages are pre-sanitized; other failures stay type-only (no paths).</summary>
    private static string DescribeError(Exception exception)
        => exception switch
        {
            AdoRequestException or RetryExhaustedException or InvalidContinuationTokenException or InvalidDetailPayloadException or PipelinePausedException => exception.Message,
            _ => $"Retrieval failed: {exception.GetType().Name}."
        };

    /// <summary>ADR-62: keeps the pause reason and its typed RetryAfter/RemainingBudget in the artifact.</summary>
    private static string DescribePause(PipelinePausedException exception)
        => exception switch
        {
            { Reason: PauseReason.RetryAfterTooLong, RetryAfter: { } retryAfter } => $"{exception.Message} RetryAfter={retryAfter}.",
            { Reason: PauseReason.RunCapReached, RemainingBudget: { } remainingBudget } => $"{exception.Message} RemainingBudget={remainingBudget}.",
            _ => exception.Message
        };
}
