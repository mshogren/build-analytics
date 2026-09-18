using System.Net;
using System.Net.Http;
using System.Text;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.Tests.AzureDevOps;

/// <summary>Deterministic HTTP handler: no sockets, one scripted responder per attempt.</summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<Uri> RequestUris { get; } = [];

    public int RequestCount => RequestUris.Count;

    public ScriptedHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public ScriptedHttpMessageHandler EnqueueJson(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? continuationToken = null,
        string? retryAfter = null,
        string? requestId = null)
        => Enqueue(_ => BuildResponse(json, statusCode, continuationToken, retryAfter, requestId));

    public ScriptedHttpMessageHandler EnqueueStatus(
        HttpStatusCode statusCode,
        string? continuationToken = null,
        string? retryAfter = null,
        string? requestId = null)
        => Enqueue(_ => BuildResponse("{}", statusCode, continuationToken, retryAfter, requestId));

    public ScriptedHttpMessageHandler EnqueueThrow(Func<Exception> exceptionFactory)
        => Enqueue(_ => throw exceptionFactory());

    private static HttpResponseMessage BuildResponse(
        string json,
        HttpStatusCode statusCode,
        string? continuationToken,
        string? retryAfter,
        string? requestId)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (continuationToken is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", continuationToken);
        }

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ms-request-id", requestId);
        }

        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI missing."));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left.");
        }

        return Task.FromResult(_responders.Dequeue()(request));
    }
}

internal sealed class FakeDelayScheduler : IDelayScheduler
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
