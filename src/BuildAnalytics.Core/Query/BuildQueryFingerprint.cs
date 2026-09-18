using System.Security.Cryptography;
using System.Text;

namespace BuildAnalytics.Core.Query;

/// <summary>
/// Computes the stable identity of an effective query: org/project/detailPolicy/apiVersion (ADR-99).
/// </summary>
public static class BuildQueryFingerprint
{
    public static string Compute(BuildQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var canonical = string.Join(
            "\n",
            $"org={query.Organization}",
            $"project={query.Project}",
            $"detailPolicy={query.DetailPolicy}",
            $"apiVersion={query.ApiVersion}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
