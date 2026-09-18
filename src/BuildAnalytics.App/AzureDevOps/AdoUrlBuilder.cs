using System.Globalization;
using System.Text;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Builds absolute Azure DevOps request URIs and the relative paths used in error text.
/// Query keys are literal (only values are percent-escaped) so <c>$top</c> stays readable.
/// </summary>
public static class AdoUrlBuilder
{
    public static string ListPath(string project)
        => $"/{Uri.EscapeDataString(project)}/_apis/build/builds";

    public static string DetailPath(string project, int runId)
        => $"/{Uri.EscapeDataString(project)}/_apis/build/builds/{runId.ToString(CultureInfo.InvariantCulture)}";

    public static Uri ListUri(BuildQuery query, int top, string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("api-version", query.ApiVersion),
            new("$top", top.ToString(CultureInfo.InvariantCulture)),
            new("queryOrder", "queueTimeDescending")
        };

        if (continuationToken is not null)
        {
            parameters.Add(new("continuationToken", continuationToken));
        }

        return Build(query.Organization, ListPath(query.Project), parameters);
    }

    public static Uri DetailUri(BuildQuery query, int runId)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Build(
            query.Organization,
            DetailPath(query.Project, runId),
            [new("api-version", query.ApiVersion)]);
    }

    private static Uri Build(string organization, string path, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder();
        builder.Append(organization.TrimEnd('/'));
        builder.Append(path);

        for (var i = 0; i < parameters.Count; i++)
        {
            builder.Append(i == 0 ? '?' : '&');
            builder.Append(parameters[i].Key);
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameters[i].Value));
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }
}
