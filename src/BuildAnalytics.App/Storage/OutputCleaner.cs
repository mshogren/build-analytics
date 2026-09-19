namespace BuildAnalytics.App.Storage;

/// <summary>
/// ADR-110: each invocation is stateless. Before retrieving, delete the previous run log and
/// the previous report file. Nothing else in the output directory is touched.
/// </summary>
public static class OutputCleaner
{
    public static void Clear(string outputRoot, string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        DeleteIfExists(Path.Combine(Path.GetFullPath(outputRoot), FileRunStore.LogFileName));
        DeleteIfExists(reportPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
