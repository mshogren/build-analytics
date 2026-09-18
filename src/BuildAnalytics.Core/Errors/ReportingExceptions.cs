using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Errors;

/// <summary>
/// Reporting needs a completed retrieval. An absent manifest or any non-completed
/// status is a typed operator error (ADR-75), never a silently empty report.
/// </summary>
public sealed class ReportingErrorException : Exception
{
    public ReportingErrorException()
        : base("Reporting requires a completed retrieval; the manifest is absent.")
    {
    }

    public ReportingErrorException(ManifestStatus? status)
        : base(status is null
            ? "Reporting requires a completed retrieval; the manifest is absent."
            : $"Reporting requires a completed retrieval; the manifest is '{status.Value}'.")
    {
        Status = status;
    }

    public ManifestStatus? Status { get; }
}
