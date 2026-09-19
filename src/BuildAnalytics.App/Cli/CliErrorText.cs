using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// ADR-94: maps any escaping exception to a sanitized, stack-trace-free message. Typed,
/// pre-sanitized errors keep their text; storage failures and unknowns stay generic so no
/// absolute path or PAT can leak.
/// </summary>
public static class CliErrorText
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ReportingWriteException
                or AdoRequestException
                or RetryExhaustedException
                or InvalidContinuationTokenException
                or RetrievalStoppedException => exception.Message,
            StorageException => "A storage failure occurred.",
            _ => $"Unexpected failure: {exception.GetType().Name}."
        };
    }
}
