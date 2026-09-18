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
/// List -> detail-if-needed -> durable run write -> manifest checkpoint (ADR-4/66..93).
/// Owns paging, token-cycle detection, and terminal status; the adapter owns the run
/// budget and HTTP retry.
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
        var isCompleted = existing is { Status: ManifestStatus.Completed };

        // ADR-88: resolve + fingerprint + compatibility happen BEFORE the completed short-circuit,
        // so a different query on a completed root is a typed error, not a silent no-op.
        var resolvedIds = await resolver.ResolveAsync(query, query.DefinitionNames, cancellationToken).ConfigureAwait(false);
        var effective = query with { ResolvedDefinitionIds = resolvedIds };
        var fingerprint = BuildQueryFingerprint.Compute(effective);

        var existingRunIds = await runs.ListRunIdsAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // ADR-92: a completed root is query-bound even with an empty runs/.
            ManifestCompatibility.EnsureCompatible(existing, fingerprint, existingRunIds, requireMatch: isCompleted);
        }

        if (isCompleted)
        {
            return new RetrievalResult(
                ManifestStatus.Completed,
                existing!.Cursor,
                ShortCircuited: true,
                PagesFetched: 0,
                RunsWritten: 0,
                existing.FailedRunIds,
                Pause: null,
                RetryAfter: null,
                RemainingBudget: null);
        }

        var createdAt = existing?.CreatedAt ?? clock.GetUtcNow();
        var failedRunIds = new List<int>(existing?.FailedRunIds ?? []);
        var existingIdSet = new HashSet<int>(existingRunIds);
        var writtenThisPass = new HashSet<int>();
        var cursor = existing?.Cursor;

        var pagesFetched = 0;
        var runsWritten = 0;

        async Task<RetrievalResult> PauseAsync(PipelinePausedException exception)
        {
            // ADR-62: paused is resumable and never completed; the reason and typed
            // RetryAfter/RemainingBudget are surfaced structurally and in lastError.
            await CommitAsync(ManifestStatus.Paused, cursor, DescribePause(exception)).ConfigureAwait(false);
            return Result(ManifestStatus.Paused, cursor, pagesFetched, runsWritten, failedRunIds, exception.Reason, exception.RetryAfter, exception.RemainingBudget);
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

        try
        {
            // ADR-68: a fingerprinted, resumable root exists before the first list call.
            await CommitAsync(ManifestStatus.InProgress, cursor, lastError: null).ConfigureAwait(false);

            var restarted = false;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);

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
                        await CommitAsync(ManifestStatus.Failed, cursor, exception.Message).ConfigureAwait(false);
                        return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
                    }

                    restarted = true;
                    seenTokens.Clear();
                    cursor = null;
                    await CommitAsync(ManifestStatus.InProgress, cursor: null, lastError: null).ConfigureAwait(false);
                    continue;
                }

                pagesFetched++;

                foreach (var listed in page.Runs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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
                        if (onDisk is not null && !DetailPolicyEvaluator.NeedsDetail(onDisk, effective.DetailPolicy))
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
                    writtenThisPass.Add(run.Id);
                }

                var next = page.ContinuationToken;
                if (next is not null && !seenTokens.Add(next))
                {
                    // ADR-85: any revisited token (including a >1 page cycle) is a token failure.
                    if (restarted)
                    {
                        await CommitAsync(ManifestStatus.Failed, cursor, "Azure DevOps returned a repeated continuation token.").ConfigureAwait(false);
                        return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
                    }

                    restarted = true;
                    seenTokens.Clear();
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
            await CommitAsync(ManifestStatus.Failed, cursor, DescribeError(exception)).ConfigureAwait(false);
            return Result(ManifestStatus.Failed, cursor, pagesFetched, runsWritten, failedRunIds);
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
        string? cursor,
        int pagesFetched,
        int runsWritten,
        IReadOnlyList<int> failedRunIds,
        PauseReason? pause = null,
        TimeSpan? retryAfter = null,
        int? remainingBudget = null)
        => new(status, cursor, ShortCircuited: false, pagesFetched, runsWritten, failedRunIds.ToArray(), pause, retryAfter, remainingBudget);

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
