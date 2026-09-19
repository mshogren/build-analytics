namespace BuildAnalytics.Core.Errors;

/// <summary>
/// The report destination could not be written. The message carries only the file name and a
/// sanitized reason - never the absolute path or its directory (ADR-95).
/// </summary>
public sealed class ReportingWriteException : Exception
{
    public ReportingWriteException(string fileName, string reason, Exception? innerException = null)
        : base($"Could not write the report file '{fileName}': {reason}", innerException)
    {
        FileName = fileName;
        Reason = reason;
    }

    public string FileName { get; }

    public string Reason { get; }
}
