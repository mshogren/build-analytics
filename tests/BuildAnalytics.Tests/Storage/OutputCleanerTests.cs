using BuildAnalytics.App.Storage;

namespace BuildAnalytics.Tests.Storage;

public sealed class OutputCleanerTests
{
    [Fact]
    public void Clear_deletes_the_log_and_report_but_nothing_else()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var reportPath = Path.Combine(root.Path, "report.xlsx");
        var unrelated = Path.Combine(root.Path, "keep-me.txt");
        var nested = Path.Combine(root.Path, "sub", "nested.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);

        File.WriteAllText(logPath, "old");
        File.WriteAllText(reportPath, "old");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(nested, "keep");

        OutputCleaner.Clear(root.Path, reportPath);

        Assert.False(File.Exists(logPath));
        Assert.False(File.Exists(reportPath));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Clear_is_a_no_op_when_neither_file_exists()
    {
        using var root = new TempOutputRoot();

        OutputCleaner.Clear(root.Path, Path.Combine(root.Path, "report.xlsx"));

        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public void Clear_deletes_a_report_outside_the_output_root_only_at_that_path()
    {
        using var root = new TempOutputRoot();
        using var other = new TempOutputRoot();
        var keep = Path.Combine(other.Path, "keep.xlsx");
        File.WriteAllText(keep, "keep");

        var report = Path.Combine(other.Path, "report.xlsx");
        File.WriteAllText(report, "old");

        OutputCleaner.Clear(root.Path, report);

        Assert.False(File.Exists(report));
        Assert.True(File.Exists(keep));
    }
}
