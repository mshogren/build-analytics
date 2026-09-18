using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>List-style and detail-style requests differ in how 404/429 are surfaced.</summary>
public enum AdoRequestKind
{
    List,
    Detail
}

/// <summary>A successful ADO response body plus the paging continuation token.</summary>
public sealed record AdoResponse(string Body, string? ContinuationToken);

/// <summary>
/// Owns the retry/backoff policy for ADO HTTP calls: 5 attempts with 1/2/4/8s waits,
/// both <c>Retry-After</c> forms, transient-exception retry, and typed error mapping.
/// </summary>
public sealed class AdoRequestExecutor
{
    public const int MaxAttempts = 5;
    public const string ContinuationTokenHeader = "x-ms-continuationtoken";

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly IDelayScheduler _delayScheduler;
    private readonly TimeProvider _timeProvider;

    public AdoRequestExecutor(HttpClient httpClient, IDelayScheduler delayScheduler, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(delayScheduler);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _delayScheduler = delayScheduler;
        _timeProvider = timeProvider;
    }

    public async Task<AdoResponse> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string requestPath,
        AdoRequestKind kind,
        int? runId,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                lastTransient = exception;
            }

            if (response is null)
            {
                if (attempt == MaxAttempts)
                {
                    throw new RetryExhaustedException(attempt, requestPath, lastTransient);
                }

                await _delayScheduler.DelayAsync(Backoff[attempt - 1], cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return new AdoResponse(body, ReadContinuationToken(response));
                }

                if (IsRetryableStatus(response.StatusCode))
                {
                    var retryAfter = ParseRetryAfter(response);

                    if (retryAfter is { } wait && wait > MaxRetryAfter)
                    {
                        throw new PipelinePausedException(PauseReason.RetryAfterTooLong, retryAfter: wait);
                    }

                    if (attempt == MaxAttempts)
                    {
                        if (kind == AdoRequestKind.Detail && response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            throw new PipelinePausedException(PauseReason.DetailThrottled);
                        }

                        throw new RetryExhaustedException(attempt, requestPath, status, ReadCorrelationId(response), lastTransient);
                    }

                    await _delayScheduler
                        .DelayAsync(retryAfter ?? Backoff[attempt - 1], cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (kind == AdoRequestKind.Detail && response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new RunNotFoundException(runId ?? 0);
                }

                if (kind == AdoRequestKind.List
                    && response.StatusCode == HttpStatusCode.BadRequest
                    && continuationToken is not null)
                {
                    // ADR-72: a 400 is a token error only when a token was actually sent.
                    throw new InvalidContinuationTokenException(continuationToken);
                }

                throw new AdoRequestException(status, requestPath, ReadCorrelationId(response));
            }
        }

        // Unreachable: the loop either returns or throws on the final attempt.
        throw new RetryExhaustedException(MaxAttempts, requestPath, lastTransient);
    }

    private static bool IsRetryableStatus(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            // A cancelled caller token is rethrown, never retried; a timeout is transient (ADR-54).
            return !cancellationToken.IsCancellationRequested;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        foreach (var raw in values)
        {
            var value = raw.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            // delta-seconds wins over HTTP-date; a negative delta clamps to 0 (retry immediately).
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                var delta = date - _timeProvider.GetUtcNow();
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }
        }

        return null;
    }

    private static string? ReadContinuationToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(ContinuationTokenHeader, out var values))
        {
            return null;
        }

        // ADR-87: a null/whitespace header means no token, not a real empty token.
        var token = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string? ReadCorrelationId(HttpResponseMessage response)
        => FirstHeader(response, "x-ms-request-id")
            ?? FirstHeader(response, "x-vss-activity-id")
            ?? FirstHeader(response, "x-ms-correlation-request-id");

    private static string? FirstHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
