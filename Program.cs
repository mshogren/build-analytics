using System.Globalization;

await BuildAnalyticsTool.RunAsync(args);

static class BuildAnalyticsTool
{
    public static async Task RunAsync(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "export";
        var commandArgs = command is "export" or "spreadsheet" ? args.Skip(1).ToArray() : args;

        switch (command)
        {
            case "export":
                await new BuildAnalyticsExporter(BuildAnalyticsExportOptions.Parse(commandArgs)).RunAsync();
                break;
            case "spreadsheet":
                await new BuildAnalyticsSpreadsheetExporter(BuildAnalyticsSpreadsheetOptions.Parse(commandArgs)).RunAsync();
                break;
            case "help":
            case "--help":
            case "-h":
            case "-?":
            default:
                PrintHelp();
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ADO build-analytics tool");
        Console.WriteLine();
        Console.WriteLine("Config:");
        Console.WriteLine("  default config file: build-analytics.config.json");
        Console.WriteLine("  env vars override config, CLI args override both");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  export      Export run.json files from Azure DevOps");
        Console.WriteLine("  spreadsheet Build an Excel workbook from exported run.json files");
        Console.WriteLine();
        Console.WriteLine("Export examples:");
        Console.WriteLine("  dotnet run -- export --output-root /workspace/build-analytics-full");
        Console.WriteLine();
        Console.WriteLine("Spreadsheet examples:");
        Console.WriteLine("  dotnet run -- spreadsheet --input-root /workspace/build-analytics-full --output /workspace/build-analytics-full.xlsx");
    }
}
