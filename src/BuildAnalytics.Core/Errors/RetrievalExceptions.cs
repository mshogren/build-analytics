namespace BuildAnalytics.Core.Errors;

/// <summary>Why a retrieval run stopped early.</summary>
public enum StopReason
{
    RetryAfterTooLong,
    RunCapReached
}

/// <summary>
/// The adapter stopped the run: the run-cap budget was reached or the server asked
/// for a retry delay over 60s. Nothing is persisted; the CLI prints the reason and
/// exits <c>1</c>.
/// </summary>
public sealed class RetrievalStoppedException : Exception
{
    public RetrievalStoppedException(StopReason reason)
        : this(reason, retryAfter: null, remainingBudget: null, innerException: null)
    {
    }

    public RetrievalStoppedException(StopReason reason, Exception? innerException)
        : this(reason, retryAfter: null, remainingBudget: null, innerException)
    {
    }

    public RetrievalStoppedException(StopReason reason, TimeSpan? retryAfter = null, int? remainingBudget = null, Exception? innerException = null)
        : base(MessageFor(reason), innerException)
    {
        Reason = reason;
        RetryAfter = retryAfter;
        RemainingBudget = remainingBudget;
    }

    public StopReason Reason { get; }

    /// <summary>Server-requested wait for <see cref="StopReason.RetryAfterTooLong"/>.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Remaining run budget for <see cref="StopReason.RunCapReached"/>.</summary>
    public int? RemainingBudget { get; }

    private static string MessageFor(StopReason reason) => reason switch
    {
        StopReason.RetryAfterTooLong =>
            "Retrieval stopped: Azure DevOps asked for a retry delay longer than 60s.",
        StopReason.RunCapReached =>
            "Retrieval stopped: the maxRuns budget was reached.",
        _ => "Retrieval stopped."
    };
}

/// <summary>
/// Azure DevOps rejected the continuation token, or handed back the same token
/// it was given. The adapter never restarts on its own; the pipeline decides.
/// </summary>
public sealed class InvalidContinuationTokenException : Exception
{
    public InvalidContinuationTokenException()
        : base("The Azure DevOps continuation token was rejected or repeated.")
    {
    }

    public InvalidContinuationTokenException(string? continuationToken)
        : base("The Azure DevOps continuation token was rejected or repeated.")
    {
        ContinuationToken = continuationToken;
    }

    public InvalidContinuationTokenException(string? continuationToken, Exception? innerException)
        : base("The Azure DevOps continuation token was rejected or repeated.", innerException)
    {
        ContinuationToken = continuationToken;
    }

    /// <summary>The offending token. Never logged verbatim by callers.</summary>
    public string? ContinuationToken { get; }
}

/// <summary>All retry attempts were consumed without a usable response.</summary>
public sealed class RetryExhaustedException : Exception
{
    public RetryExhaustedException(int attempts, string requestPath, Exception? innerException = null)
        : this(attempts, requestPath, statusCode: null, correlationId: null, innerException)
    {
    }

    public RetryExhaustedException(
        int attempts,
        string requestPath,
        int? statusCode,
        string? correlationId,
        Exception? innerException = null)
        : base(MessageFor(attempts, statusCode, requestPath, correlationId), innerException)
    {
        Attempts = attempts;
        RequestPath = requestPath;
        StatusCode = statusCode;
        CorrelationId = correlationId;
    }

    public int Attempts { get; }

    /// <summary>Sanitized relative request path (no host or query string).</summary>
    public string RequestPath { get; }

    public int? StatusCode { get; }

    public string? CorrelationId { get; }

    private static string MessageFor(int attempts, int? statusCode, string requestPath, string? correlationId)
    {
        var status = statusCode is null ? "no response" : $"status {statusCode}";
        return AdoErrorText.Format(
            $"Azure DevOps request exhausted {attempts} attempts ({status})",
            requestPath,
            correlationId);
    }
}

/// <summary>A non-retryable Azure DevOps HTTP failure.</summary>
public sealed class AdoRequestException : Exception
{
    public AdoRequestException(int statusCode, string requestPath, string? correlationId, Exception? innerException = null)
        : base(AdoErrorText.Format($"Azure DevOps request failed with status {statusCode}", requestPath, correlationId), innerException)
    {
        StatusCode = statusCode;
        RequestPath = requestPath;
        CorrelationId = correlationId;
    }

    /// <summary>A 2xx response whose body could not be parsed (ADR-84).</summary>
    public AdoRequestException(string requestPath, Exception? innerException)
        : base(AdoErrorText.Format("Azure DevOps returned a malformed response", requestPath, null), innerException)
    {
        StatusCode = 0;
        RequestPath = requestPath;
        CorrelationId = null;
    }

    public int StatusCode { get; }

    /// <summary>Sanitized relative request path (no host or query string).</summary>
    public string RequestPath { get; }

    public string? CorrelationId { get; }
}
/// <summary>
/// Builds error text from a status, a sanitized relative URL, and a correlation id only.
/// The raw Azure DevOps body, the PAT, and absolute URLs are never included.
/// </summary>
public static class AdoErrorText
{
    public static string Format(string prefix, string requestPath, string? correlationId)
    {
        var path = SanitizePath(requestPath);
        return string.IsNullOrWhiteSpace(correlationId)
            ? $"{prefix} for '{path}'."
            : $"{prefix} for '{path}' (correlation id {correlationId}).";
    }

    /// <summary>Keeps only the path component, rejecting absolute URLs and query strings.</summary>
    private static string SanitizePath(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return "/";
        }

        var path = requestPath;
        if (Uri.TryCreate(requestPath, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        return path.Length == 0 ? "/" : path;
    }
}
