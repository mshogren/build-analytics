using System.Net.Http;

namespace BuildAnalytics.App.Cli;

/// <summary>Supplies the Azure DevOps PAT. Production reads <c>AZDO_PAT</c>; tests inject a fake.</summary>
public interface ICredentialProvider
{
    string? GetPat();
}

/// <summary>Environment-backed credential provider. The PAT is never a flag and never persisted.</summary>
public sealed class EnvironmentCredentialProvider : ICredentialProvider
{
    public const string VariableName = "AZDO_PAT";

    public string? GetPat() => Environment.GetEnvironmentVariable(VariableName);
}

/// <summary>Output channels: usage/reports to stdout, progress/errors to stderr (ADR-82).</summary>
public interface IConsoleOutput
{
    void WriteLine(string message);

    void WriteError(string message);
}

public sealed class SystemConsoleOutput : IConsoleOutput
{
    public void WriteLine(string message) => Console.Out.WriteLine(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);
}

/// <summary>Creates the HTTP transport so tests can supply a scripted handler (no sockets).</summary>
public interface IHttpMessageHandlerFactory
{
    HttpMessageHandler Create();
}

public sealed class DefaultHttpMessageHandlerFactory : IHttpMessageHandlerFactory
{
    public HttpMessageHandler Create() => new HttpClientHandler();
}
