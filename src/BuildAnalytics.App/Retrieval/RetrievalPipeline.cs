using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Retrieval;

/// <summary>Outcome of one retrieval pass. <see cref="FailedRunIds"/> are list-time 404s skipped this pass.</summary>
public sealed record RetrievalResult(int PagesFetched, int RunsWritten, IReadOnlyList<int> FailedRunIds);

/// <summary>
/// List (paging from the beginning) -> detail-if-needed -> durable append (ADR-110).
/// There is no manifest, lock, resume, fingerprint or early stop: every run re-retrieves the
/// full history. The adapter owns the run budget and HTTP retry.
/// </summary>
public sealed class RetrievalPipeline(
    IBuildSource source,
    IRunStore runs,
    IRetrievalProgress? progress = null)
{
    private readonly IRetrievalProgress _progress = progress ?? NullRetrievalProgress.Instance;

    public async Task<RetrievalResult> RunAsync(BuildQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // The log was cleared before this pass (ADR-110). A token-restart replay must not
        // re-append or re-fetch detail for a run already handled in this same pass.
        var processed = new HashSet<int>();
        var failedRunIds = new List<int>();
        var pagesFetched = 0;
        var runsWritten = 0;
        string? continuationToken = null;
        var restarted = false;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        _progress.Started();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BuildPage page;
            try
            {
                page = await source.ListAsync(query, continuationToken, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidContinuationTokenException)
            {
                if (restarted)
                {
                    throw;
                }

                // ADR-85: restart the pass once from the beginning (null), then fail.
                restarted = true;
                seenTokens.Clear();
                continuationToken = null;
                _progress.Restarting();
                continue;
            }

            pagesFetched++;
            _progress.PageFetched(pagesFetched, page.Runs.Count);

            var pageRuns = new List<BuildRun>();
            foreach (var listed in page.Runs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processed.Contains(listed.Id))
                {
                    continue;
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
                        // A missing run is skipped and recorded; it does not block the page.
                        failedRunIds.Add(run.Id);
                        processed.Add(run.Id);
                        continue;
                    }
                }

                pageRuns.Add(run);
                processed.Add(run.Id);
                runsWritten++;
            }

            // Persist the page durably before moving on.
            if (pageRuns.Count > 0)
            {
                await runs.AppendAsync(pageRuns, cancellationToken).ConfigureAwait(false);
            }

            var next = page.ContinuationToken;
            if (next is not null && !seenTokens.Add(next))
            {
                // ADR-85: any revisited token is a token failure (restart once, then fail).
                if (restarted)
                {
                    throw new InvalidContinuationTokenException(next);
                }

                restarted = true;
                seenTokens.Clear();
                continuationToken = null;
                _progress.Restarting();
                continue;
            }

            if (next is null)
            {
                break;
            }

            continuationToken = next;
        }

        _progress.Completed(pagesFetched, runsWritten);
        return new RetrievalResult(pagesFetched, runsWritten, failedRunIds.ToArray());
    }
}
