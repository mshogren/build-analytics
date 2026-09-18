using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Pure decision for the ADO detail fallback. <see cref="DetailPolicy.ListOnly"/> never
/// needs detail; <see cref="DetailPolicy.FillMissing"/> needs it only for a missing
/// contract field or a completed run with a missing timestamp.
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
            || run.DefinitionName is null
            || run.Status is null
            || run.Result is null)
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
