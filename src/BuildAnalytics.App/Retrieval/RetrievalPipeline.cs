using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Retrieval;

/// <summary>Outcome of one retrieval run. Non-completed statuses are resumable (ADR-67/70).</summary>
public sealed record RetrievalResult(
    ManifestStatus Status,
    string? Cursor,
    bool ShortCircuited,
    int PagesFetched,
    int RunsWritten,
    IReadOnlyList<int> FailedRunIds,
    PauseReason? Pause,
    TimeSpan? RetryAfter,
    int? RemainingBudget);

/// <summary>
/// List -> detail-if-needed -> durable run write -> manifest checkpoint (ADR-4/66..71).
/// Owns paging, repeated/invalid token restart, and terminal status; the adapter owns
/// the run budget and HTTP retry.
/// </summary>
public sealed class RetrievalPipeline(
    IBuildSource source,
    IDefinitionResolver resolver,
    IRunStore runs,
    IManifestStore manifests,
    TimeProvider clock)
{
    public async Task<RetrievalResult> RunAsync(BuildQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await manifests.TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { Status: ManifestStatus.Completed })
        {
            // ADR-71: a completed manifest short-circuits before resolving ids or calling the source.
            return new RetrievalResult(
                ManifestStatus.Completed,
                existing.Cursor,
                ShortCircuited: true,
                PagesFetched: 0,
                RunsWritten: 0,
                existing.FailedRunIds,
                Pause: null,
                RetryAfter: null,
                RemainingBudget: null);
        }

        var resolvedIds = await resolver.ResolveAsync(query, query.DefinitionNames, cancellationToken).ConfigureAwait(false);
        var effective = query with { ResolvedDefinitionIds = resolvedIds };
        var fingerprint = BuildQueryFingerprint.Compute(effective);

        var existingRunIds = await runs.ListRunIdsAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // ADR-3/66: only a non-empty runs/ with a different fingerprint is a conflict.
            ManifestCompatibility.EnsureCompatible(existing, fingerprint, existingRunIds);
        }

        var createdAt = existing?.CreatedAt ?? clock.GetUtcNow();
        var failedRunIds = new List<int>(existing?.FailedRunIds ?? []);
        var onDiskRunIds = new HashSet<int>(existingRunIds);
        var cursor = existing?.Cursor;

        // ADR-68: a fingerprinted, resumable root exists before the first list call.
        await CommitAsync(ManifestStatus.InProgress, cursor, lastError: null).ConfigureAwait(false);

        var pagesFetched = 0;
        var runsWritten = 0;
        var restarted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BuildPage page;
            try
            {
                page = await source.ListAsync(effective, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidContinuationTokenException exception)
            {
                if (restarted)
                {
                    // ADR-69: never loop; a second invalid token fails the run.
                    await CommitAsync(ManifestStatus.Failed, cursor, exception.Message).ConfigureAwait(false);
                    return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
                }

                restarted = true;
                cursor = null;
                await CommitAsync(ManifestStatus.InProgress, cursor: null, lastError: null).ConfigureAwait(false);
                continue;
            }
            catch (PipelinePausedException exception)
            {
                return await PauseAsync(cursor, pagesFetched, runsWritten, failedRunIds, exception).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ADR-70: leave the last committed status; no advance, no cleanup commit.
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                await CommitAsync(ManifestStatus.Failed, cursor, DescribeError(exception)).ConfigureAwait(false);
                return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
            }

            pagesFetched++;

            try
            {
                foreach (var listed in page.Runs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // ADR-7/R11: on a rebuild/replay, never re-fetch detail or overwrite an
                    // on-disk file the active policy already accepts (that would downgrade a
                    // detail source back to a thin list row).
                    if (onDiskRunIds.Contains(listed.Id))
                    {
                        var existingRun = await TryReadExistingRunAsync(listed.Id, cancellationToken).ConfigureAwait(false);
                        if (existingRun is not null
                            && !DetailPolicyEvaluator.NeedsDetail(existingRun, effective.DetailPolicy))
                        {
                            continue;
                        }
                    }

                    var run = listed;
                    if (DetailPolicyEvaluator.NeedsDetail(run, effective.DetailPolicy))
                    {
                        try
                        {
                            run = await source.GetDetailAsync(effective, run.Id, cancellationToken).ConfigureAwait(false);
                        }
                        catch (RunNotFoundException)
                        {
                            // ADR-67: a missing run is skipped, recorded, and does not block the page.
                            if (!failedRunIds.Contains(run.Id))
                            {
                                failedRunIds.Add(run.Id);
                            }

                            continue;
                        }
                    }

                    await runs.WriteAsync(run, cancellationToken).ConfigureAwait(false);
                    runsWritten++;
                    onDiskRunIds.Add(run.Id);
                }
            }
            catch (PipelinePausedException exception)
            {
                return await PauseAsync(cursor, pagesFetched, runsWritten, failedRunIds, exception).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // ADR-67: a run write that fails never advances the checkpoint.
                await CommitAsync(ManifestStatus.Failed, cursor, DescribeError(exception)).ConfigureAwait(false);
                return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
            }

            var next = page.ContinuationToken;
            if (next is not null && string.Equals(next, cursor, StringComparison.Ordinal))
            {
                if (restarted)
                {
                    // ADR-69: a second repeated token fails the run.
                    await CommitAsync(ManifestStatus.Failed, cursor, "Azure DevOps returned a repeated continuation token.").ConfigureAwait(false);
                    return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
                }

                restarted = true;
                cursor = null;
                await CommitAsync(ManifestStatus.InProgress, cursor: null, lastError: null).ConfigureAwait(false);
                continue;
            }

            if (next is null)
            {
                // ADR-67: exhausted token with failures is still completed.
                await CommitAsync(ManifestStatus.Completed, cursor: null, lastError: null).ConfigureAwait(false);
                return Result(ManifestStatus.Completed, cursor: null, pagesFetched, runsWritten, failedRunIds);
            }

            cursor = next;
            await CommitAsync(ManifestStatus.InProgress, cursor, lastError: null).ConfigureAwait(false);
        }

        async Task<RetrievalResult> PauseAsync(
            string? pauseCursor,
            int pages,
            int written,
            IReadOnlyList<int> failed,
            PipelinePausedException exception)
        {
            // ADR-62: paused is resumable and never completed; the reason and typed
            // RetryAfter/RemainingBudget are surfaced structurally and in lastError.
            await CommitAsync(ManifestStatus.Paused, pauseCursor, DescribePause(exception)).ConfigureAwait(false);
            return Result(
                ManifestStatus.Paused,
                pauseCursor,
                pages,
                written,
                failed,
                exception.Reason,
                exception.RetryAfter,
                exception.RemainingBudget);
        }

        async Task CommitAsync(ManifestStatus status, string? cursor, string? lastError)
        {
            var manifest = new Manifest(
                Manifest.CurrentSchemaVersion,
                fingerprint,
                status,
                cursor,
                createdAt,
                clock.GetUtcNow(),
                lastError,
                failedRunIds,
                query.DefinitionIds,
                query.DefinitionNames);

            await manifests.CommitAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>ADR-7/R11: a corrupt or unreadable existing run is treated as missing and re-fetched.</summary>
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
        catch (IOException)
        {
            return null;
        }
    }

    private static RetrievalResult Result(
        ManifestStatus status,
        string? cursor,
        int pagesFetched,
        int runsWritten,
        IReadOnlyList<int> failedRunIds,
        PauseReason? pause = null,
        TimeSpan? retryAfter = null,
        int? remainingBudget = null)
        => new(status, cursor, ShortCircuited: false, pagesFetched, runsWritten, failedRunIds.ToArray(), pause, retryAfter, remainingBudget);

    private static bool IsRecoverable(Exception exception)
        => exception is RetryExhaustedException
            or AdoRequestException
            or StorageException
            or IOException
            or UnauthorizedAccessException;

    /// <summary>ADR-27: typed ADO messages are pre-sanitized; other failures stay type-only (no paths).</summary>
    private static string DescribeError(Exception exception)
        => exception switch
        {
            AdoRequestException or RetryExhaustedException or InvalidContinuationTokenException or PipelinePausedException => exception.Message,
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
