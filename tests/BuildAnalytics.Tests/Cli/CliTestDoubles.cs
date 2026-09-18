using System.Net.Http;
using BuildAnalytics.App.Cli;

namespace BuildAnalytics.Tests.Cli;

internal sealed class CapturingConsole : IConsoleOutput
{
    public List<string> Stdout { get; } = [];

    public List<string> Stderr { get; } = [];

    public void WriteLine(string message) => Stdout.Add(message);

    public void WriteError(string message) => Stderr.Add(message);
}

internal sealed class FakeCredentialProvider(string? pat) : ICredentialProvider
{
    public string? Pat { get; set; } = pat;

    public string? GetPat() => Pat;
}

internal sealed class CountingHandlerFactory(Func<HttpMessageHandler> factory) : IHttpMessageHandlerFactory
{
    public int Created { get; private set; }

    public HttpMessageHandler Create()
    {
        Created++;
        return factory();
    }
}

internal sealed class TrackingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public bool Disposed { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
