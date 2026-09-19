namespace BuildAnalytics.App.Cli;

/// <summary>
/// Raw values loaded from a config file (ADR-98). Every field is optional; the pure parser
/// merges them with CLI values and built-in defaults. A missing <c>pat</c> is the only
/// credential source (<c>AZDO_PAT</c>).
/// </summary>
public sealed record BuildAnalyticsConfig(
    string? Organization = null,
    string? Project = null,
    string? OutputRoot = null,
    string? ApiVersion = null,
    int? MaxRuns = null,
    bool? Quiet = null,
    string? Out = null);

/// <summary>Outcome of <see cref="ConfigLoader.Load"/>: either config values or a usage error message.</summary>
public sealed record ConfigLoadResult(BuildAnalyticsConfig? Config, string? Error);
