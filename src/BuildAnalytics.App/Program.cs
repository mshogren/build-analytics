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

// ADR-98: the config loader owns the IO; the parser stays pure. Config only applies to the
// retrieve/report verbs, so help and unknown commands still work with a broken config file.
var loadsConfig = args.Length > 0 && args[0] is "retrieve" or "report";
var config = loadsConfig ? ConfigLoader.Load(args) : new ConfigLoadResult(null, null);
if (config.Error is not null)
{
    Console.Error.WriteLine(config.Error);
    Console.Error.WriteLine(CliApplication.Usage);
    return 2;
}

try
{
    return await application.RunAsync(args, config.Config, cancellation.Token);
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
