using System.Text.RegularExpressions;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Converts a definition-name wildcard into an anchored, case-insensitive regex.
/// Every regex metacharacter is escaped except <c>*</c> (any run) and <c>?</c> (one char).
/// </summary>
public static class AdoWildcard
{
    public static Regex ToRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var escaped = Regex
            .Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);

        return new Regex(
            $"^{escaped}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
