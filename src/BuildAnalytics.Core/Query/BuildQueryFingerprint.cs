using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BuildAnalytics.Core.Query;

/// <summary>
/// Computes the stable identity of an effective query. Input ordering and
/// null-vs-empty collections do not affect the result.
/// </summary>
public static class BuildQueryFingerprint
{
    public static string Compute(BuildQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resolvedIds = (query.ResolvedDefinitionIds ?? [])
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var canonical = string.Join(
            "\n",
            $"org={query.Organization}",
            $"project={query.Project}",
            $"minTime={FormatInstant(query.MinTime)}",
            $"maxTime={FormatInstant(query.MaxTime)}",
            $"resolvedDefinitionIds={string.Join(',', resolvedIds)}",
            $"detailPolicy={query.DetailPolicy}",
            $"apiVersion={query.ApiVersion}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string FormatInstant(DateTimeOffset? value)
        => value is { } instant
            ? instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : "-";
}
