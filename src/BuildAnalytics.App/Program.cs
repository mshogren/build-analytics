using BuildAnalytics.App.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var application = new CliApplication(
    new EnvironmentCredentialProvider(),
    new SystemConsoleOutput(),
    new DefaultHttpMessageHandlerFactory());

try
{
    return await application.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception exception)
{
    // ADR-94: no stack trace, no PAT, no absolute path - just a sanitized message.
    Console.Error.WriteLine(CliErrorText.Describe(exception));
    return 1;
}
