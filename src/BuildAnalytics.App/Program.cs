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
