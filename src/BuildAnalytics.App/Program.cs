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

// ADR-98: the config loader owns the IO; the parser stays pure. Help is answered without
// touching the config file, so a broken config cannot hide the usage text.
var isHelp = args.Length == 0 || args[0] is "help" or "--help" or "-h";
var config = isHelp ? new ConfigLoadResult(null, null) : ConfigLoader.Load(args);
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
