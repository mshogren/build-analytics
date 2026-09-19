namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Pure run-budget arithmetic for paging. Kept separate from the HTTP adapter so the
/// <c>max(1, min(pageSize, remaining))</c> rule is directly testable.
/// </summary>
public static class AdoPageBudget
{
    /// <summary>
    /// Returns the effective <c>$top</c> for the next page, or <c>null</c> when the
    /// budget is exhausted (stop with no call).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The remaining budget is negative.</exception>
    public static int? TrimTop(int pageSize, int remaining)
    {
        if (remaining < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remaining),
                remaining,
                "The remaining run budget must not be negative.");
        }

        if (remaining == 0)
        {
            return null;
        }

        return Math.Max(1, Math.Min(pageSize, remaining));
    }
}
