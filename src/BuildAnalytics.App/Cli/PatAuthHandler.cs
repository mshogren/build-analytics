using System.Net.Http.Headers;
using System.Text;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.Cli;

/// <summary>Adds HTTP Basic auth with an empty username and the PAT as password (ADR-81).</summary>
public sealed class PatAuthHandler : DelegatingHandler
{
    private readonly AuthenticationHeaderValue _authorization;

    public PatAuthHandler(string pat)
    {
        ArgumentException.ThrowIfNullOrEmpty(pat);

        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _authorization = new AuthenticationHeaderValue("Basic", token);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = _authorization;
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Production delay seam backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemDelayScheduler : IDelayScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
