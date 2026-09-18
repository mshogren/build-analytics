namespace BuildAnalytics.Core.Query;

/// <summary>Whether per-run detail calls are allowed when list fields are insufficient.</summary>
public enum DetailPolicy
{
    ListOnly,
    FillMissing
}

/// <summary>The effective query. Identity is org/project/detailPolicy/apiVersion (ADR-99).</summary>
public sealed record BuildQuery(
    string Organization,
    string Project,
    DetailPolicy DetailPolicy,
    string ApiVersion);
