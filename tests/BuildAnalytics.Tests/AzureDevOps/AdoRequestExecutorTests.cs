using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoRequestExecutorTests
{
    private static readonly Uri RequestUri = new("https://dev.azure.com/org/project/_apis/build/builds");

    [Fact]
    public async Task Success_returns_body_and_token_without_delays()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueJson("""{ "value": [] }""", continuationToken: "next");

        var response = await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal("next", response.ContinuationToken);
        Assert.Empty(delays.Delays);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Retryable_status_is_retried(HttpStatusCode status)
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(status);
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Exhaustion_throws_typed_error_after_five_attempts()
    {
        var (executor, handler, delays) = Create();
        for (var i = 0; i < AdoRequestExecutor.MaxAttempts; i++)
        {
            handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        }

        var exception = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default));

        Assert.Equal(AdoRequestExecutor.MaxAttempts, exception.Attempts);
        Assert.Equal(500, exception.StatusCode);
        Assert.Equal([1, 2, 4, 8], delays.Delays.Select(delay => delay.TotalSeconds));
        Assert.Equal(AdoRequestExecutor.MaxAttempts, handler.RequestCount);
    }

    [Fact]
    public async Task Permanent_status_fails_fast()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(HttpStatusCode.Forbidden, requestId: "corr-1");

        var exception = await Assert.ThrowsAsync<AdoRequestException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("corr-1", exception.CorrelationId);
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/project/_apis/build/builds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("corr-1", exception.Message, StringComparison.Ordinal);
        Assert.Empty(delays.Delays);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Detail_404_is_RunNotFoundException()
    {
        var (executor, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<RunNotFoundException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds/42", AdoRequestKind.Detail, 42, null, default));

        Assert.Equal(42, exception.RunId);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task BadRequest_with_continuation_token_is_InvalidContinuationTokenException()
    {
        var (executor, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<InvalidContinuationTokenException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, "stale", default));

        Assert.Equal("stale", exception.ContinuationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetryAfter_delta_seconds_wins()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "30");
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(30)], delays.Delays);
    }

    [Fact]
    public async Task RetryAfter_http_date_is_resolved_against_the_clock()
    {
        var clock = new FakeTimeProvider { UtcNow = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var (executor, handler, delays) = Create(clock);
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "Mon, 01 Jan 2024 00:00:30 GMT");
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(30)], delays.Delays);
    }

    [Fact]
    public async Task RetryAfter_past_date_clamps_to_zero()
    {
        var clock = new FakeTimeProvider { UtcNow = new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero) };
        var (executor, handler, delays) = Create(clock);
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "Mon, 01 Jan 2024 00:00:00 GMT");
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.Zero], delays.Delays);
    }

    [Fact]
    public async Task RetryAfter_sixty_seconds_is_allowed()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "60");
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(60)], delays.Delays);
    }

    [Fact]
    public async Task RetryAfter_over_sixty_seconds_pauses()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "61");

        var exception = await Assert.ThrowsAsync<PipelinePausedException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default));

        Assert.Equal(PauseReason.RetryAfterTooLong, exception.Reason);
        Assert.Empty(delays.Delays);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Detail_throttle_pauses_the_pipeline()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, retryAfter: "5");

        var exception = await Assert.ThrowsAsync<PipelinePausedException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds/42", AdoRequestKind.Detail, 42, null, default));

        Assert.Equal(PauseReason.DetailThrottled, exception.Reason);
        Assert.Empty(delays.Delays);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Transient_exception_is_retried()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueThrow(() => new HttpRequestException("boom"));
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task IOException_is_retried()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueThrow(() => new IOException("io"));
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SocketException_is_retried()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueThrow(() => new SocketException(10054));
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Timeout_cancellation_is_retried()
    {
        var (executor, handler, delays) = Create();
        handler.EnqueueThrow(() => new OperationCanceledException("timeout"));
        handler.EnqueueJson("{}");

        await executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays.Delays);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Exception_exhaustion_throws_typed_error()
    {
        var (executor, handler, delays) = Create();
        for (var i = 0; i < AdoRequestExecutor.MaxAttempts; i++)
        {
            handler.EnqueueThrow(() => new HttpRequestException("boom"));
        }

        var exception = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default));

        Assert.Equal(AdoRequestExecutor.MaxAttempts, exception.Attempts);
        Assert.Null(exception.StatusCode);
        Assert.Equal([1, 2, 4, 8], delays.Delays.Select(delay => delay.TotalSeconds));
    }

    [Fact]
    public async Task Cancelled_caller_token_is_rethrown_without_retry()
    {
        using var cts = new CancellationTokenSource();
        var (executor, handler, delays) = Create();
        handler.Enqueue(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, cts.Token));

        Assert.Empty(delays.Delays);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Error_text_never_contains_the_raw_body()
    {
        var (executor, handler, _) = Create();
        handler.EnqueueJson("""{ "secret": "super-sensitive-payload" }""", HttpStatusCode.InternalServerError);
        for (var i = 1; i < AdoRequestExecutor.MaxAttempts; i++)
        {
            handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        }

        var exception = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => executor.SendAsync(Factory, "/project/_apis/build/builds", AdoRequestKind.List, null, null, default));

        Assert.DoesNotContain("super-sensitive-payload", exception.Message, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Factory() => new(HttpMethod.Get, RequestUri);

    private static (AdoRequestExecutor Executor, ScriptedHttpMessageHandler Handler, FakeDelayScheduler Delays) Create(TimeProvider? clock = null)
    {
        var handler = new ScriptedHttpMessageHandler();
        var delays = new FakeDelayScheduler();
        var executor = new AdoRequestExecutor(new HttpClient(handler), delays, clock ?? new FakeTimeProvider());
        return (executor, handler, delays);
    }
}
