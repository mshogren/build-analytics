using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Query;

/// <summary>
/// Pure decision for the ADO detail fallback (ADR-58). <see cref="DetailPolicy.ListOnly"/>
/// never needs detail; <see cref="DetailPolicy.FillMissing"/> needs it only for a missing
/// contract field or a completed run with a missing timestamp. Blank strings count as missing.
/// </summary>
public static class DetailPolicyEvaluator
{
    public static bool NeedsDetail(BuildRun run, DetailPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (policy == DetailPolicy.ListOnly)
        {
            return false;
        }

        if (run.DefinitionId is null
            || string.IsNullOrWhiteSpace(run.DefinitionName)
            || string.IsNullOrWhiteSpace(run.Status)
            || string.IsNullOrWhiteSpace(run.Result))
        {
            return true;
        }

        if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return run.QueueTime is null || run.StartTime is null || run.FinishTime is null;
        }

        return false;
    }
}
