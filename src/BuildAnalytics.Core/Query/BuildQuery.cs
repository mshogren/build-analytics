namespace BuildAnalytics.Core.Query;

/// <summary>The effective query. Identity is org/project/apiVersion.</summary>
public sealed record BuildQuery(
    string Organization,
    string Project,
    string ApiVersion);
