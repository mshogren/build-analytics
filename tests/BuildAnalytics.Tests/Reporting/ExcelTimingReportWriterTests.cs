using BuildAnalytics.App.Reporting;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Timing;
using BuildAnalytics.Tests.Storage;
using ClosedXML.Excel;

namespace BuildAnalytics.Tests.Reporting;

public sealed class ExcelTimingReportWriterTests
{
    [Fact]
    public void Workbook_has_overview_monthly_and_runs_sheets()
    {
        using var workbook = Open(SampleReport());

        Assert.True(workbook.Worksheets.TryGetWorksheet("Overview", out _));
        Assert.True(workbook.Worksheets.TryGetWorksheet("Monthly", out _));
        Assert.True(workbook.Worksheets.TryGetWorksheet("Runs", out _));
    }

    [Fact]
    public void Overview_lists_metrics_in_order_with_values()
    {
        using var workbook = Open(SampleReport());
        var sheet = workbook.Worksheet("Overview");

        Assert.Equal("Metric", sheet.Cell(1, 1).GetString());
        Assert.Equal("Value", sheet.Cell(1, 2).GetString());

        Assert.Equal("Runs", sheet.Cell(2, 1).GetString());
        Assert.Equal(10, sheet.Cell(2, 2).GetValue<int>());
        Assert.Equal("Succeeded", sheet.Cell(3, 1).GetString());
        Assert.Equal(6, sheet.Cell(3, 2).GetValue<int>());
        Assert.Equal("Failed", sheet.Cell(4, 1).GetString());
        Assert.Equal(2, sheet.Cell(4, 2).GetValue<int>());
        Assert.Equal("Partially Succeeded", sheet.Cell(5, 1).GetString());
        Assert.Equal("Canceled", sheet.Cell(6, 1).GetString());
        Assert.Equal("Not Started", sheet.Cell(7, 1).GetString());
        Assert.Equal("Wait > 5 Min", sheet.Cell(8, 1).GetString());
        Assert.Equal("Avg Queue Wait (sec)", sheet.Cell(9, 1).GetString());
        Assert.Equal("Avg Duration (sec)", sheet.Cell(10, 1).GetString());
        Assert.Equal("Avg Total (sec)", sheet.Cell(11, 1).GetString());

        Assert.Equal(12.35, sheet.Cell(9, 2).GetDouble(), 2);
        Assert.Equal("0.00", sheet.Cell(9, 2).Style.NumberFormat.Format);
    }

    [Fact]
    public void Monthly_preserves_month_order_with_unknown_last()
    {
        using var workbook = Open(SampleReport());
        var sheet = workbook.Worksheet("Monthly");

        Assert.Equal("Month", sheet.Cell(1, 1).GetString());
        Assert.Equal("Runs", sheet.Cell(1, 2).GetString());
        Assert.Equal("2024-01", sheet.Cell(2, 1).GetString());
        Assert.Equal(6, sheet.Cell(2, 2).GetValue<int>());
        Assert.Equal("(unknown)", sheet.Cell(3, 1).GetString());
        Assert.Equal(4, sheet.Cell(3, 2).GetValue<int>());
    }

    [Fact]
    public void Runs_sheet_has_the_exact_header()
    {
        using var workbook = Open(SampleReport());
        var sheet = workbook.Worksheet("Runs");

        string[] expected =
        [
            "RunId", "DefinitionId", "DefinitionName", "BuildNumber",
            "QueueTime", "StartTime", "FinishTime", "Status", "Result", "Reason",
            "PoolId", "PoolName", "SourceBranch",
            "QueueWaitSeconds", "RunDurationSeconds", "TotalDurationSeconds"
        ];

        for (var column = 0; column < expected.Length; column++)
        {
            Assert.Equal(expected[column], sheet.Cell(1, column + 1).GetString());
        }
    }

    [Fact]
    public void Runs_sheet_orders_by_queue_time_ascending_nulls_last_then_id()
    {
        var origin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        BuildRun[] runs =
        [
            Run(id: 3, queue: null),
            Run(id: 2, queue: origin.AddHours(2)),
            Run(id: 1, queue: origin),
            Run(id: 4, queue: origin.AddHours(1)),
            Run(id: 0, queue: null),
        ];
        var report = new TimingReport(MonthlyTimingRollup.Summarize(runs), runs);

        using var workbook = Open(report);
        var sheet = workbook.Worksheet("Runs");

        Assert.Equal(1, sheet.Cell(2, 1).GetValue<int>());
        Assert.Equal(4, sheet.Cell(3, 1).GetValue<int>());
        Assert.Equal(2, sheet.Cell(4, 1).GetValue<int>());
        Assert.Equal(0, sheet.Cell(5, 1).GetValue<int>()); // null QueueTime: RunId ascending
        Assert.Equal(3, sheet.Cell(6, 1).GetValue<int>());
    }

    [Fact]
    public void Runs_sheet_durations_match_TimingCalculator()
    {
        var origin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var run = Run(id: 7, queue: origin, start: origin.AddSeconds(30), finish: origin.AddSeconds(90));
        var report = new TimingReport(MonthlyTimingRollup.Summarize([run]), [run]);

        using var workbook = Open(report);
        var sheet = workbook.Worksheet("Runs");
        var timing = TimingCalculator.Calculate(run);

        Assert.Equal(timing.QueueWaitSeconds!.Value, sheet.Cell(2, 14).GetDouble(), 6);
        Assert.Equal(timing.RunDurationSeconds!.Value, sheet.Cell(2, 15).GetDouble(), 6);
        Assert.Equal(timing.TotalDurationSeconds!.Value, sheet.Cell(2, 16).GetDouble(), 6);
        Assert.Equal("0.00", sheet.Cell(2, 14).Style.NumberFormat.Format);
    }

    [Fact]
    public void Runs_sheet_blanks_missing_optional_values()
    {
        var run = Run(id: 7, queue: null);
        var report = new TimingReport(MonthlyTimingRollup.Summarize([run]), [run]);

        using var workbook = Open(report);
        var sheet = workbook.Worksheet("Runs");

        Assert.Equal(7, sheet.Cell(2, 1).GetValue<int>());
        Assert.True(sheet.Cell(2, 5).IsEmpty());   // QueueTime
        Assert.True(sheet.Cell(2, 14).IsEmpty());  // QueueWaitSeconds
        Assert.True(sheet.Cell(2, 16).IsEmpty());  // TotalDurationSeconds
    }

    [Fact]
    public void Null_averages_render_as_blank_cells()
    {
        var bytes = ExcelTimingReportWriter.BuildWorkbook(new TimingReport(NullAverageSummary(), []));
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var overview = workbook.Worksheet("Overview");
        var monthly = workbook.Worksheet("Monthly");

        Assert.True(overview.Cell(9, 2).IsEmpty());
        Assert.True(overview.Cell(10, 2).IsEmpty());
        Assert.True(overview.Cell(11, 2).IsEmpty());
        Assert.True(monthly.Cell(2, 9).IsEmpty());
        Assert.True(monthly.Cell(2, 10).IsEmpty());
        Assert.True(monthly.Cell(2, 11).IsEmpty());
    }

    [Fact]
    public async Task Write_produces_a_readable_workbook()
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "timing-report.xlsx");
        var writer = new ExcelTimingReportWriter(path);

        await writer.WriteAsync(SampleReport(), CancellationToken.None);

        Assert.True(File.Exists(path));
        using var workbook = new XLWorkbook(path);
        Assert.True(workbook.Worksheets.TryGetWorksheet("Overview", out _));
        Assert.True(workbook.Worksheets.TryGetWorksheet("Runs", out _));
    }

    [Theory]
    [InlineData(FileOperation.WriteTemp)]
    [InlineData(FileOperation.FlushToDisk)]
    [InlineData(FileOperation.Rename)]
    public async Task Write_stage_failure_preserves_the_prior_report_and_leaves_no_temp_file(FileOperation stage)
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "timing-report.xlsx");
        await File.WriteAllTextAsync(path, "prior", CancellationToken.None);

        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, _) => operation == stage ? new IOException("injected") : null);
        var writer = new ExcelTimingReportWriter(path, failing);

        await Assert.ThrowsAsync<ReportingWriteException>(() => writer.WriteAsync(SampleReport(), CancellationToken.None));

        Assert.Equal("prior", await File.ReadAllTextAsync(path, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task Write_failure_message_is_sanitized()
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "timing-report.xlsx");
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, failedPath) => operation == FileOperation.WriteTemp
                ? new IOException($"The process cannot access the file '{failedPath}'.")
                : null);
        var writer = new ExcelTimingReportWriter(path, failing);

        var exception = await Assert.ThrowsAsync<ReportingWriteException>(() => writer.WriteAsync(SampleReport(), CancellationToken.None));

        Assert.Equal("timing-report.xlsx", exception.FileName);
        Assert.Contains("timing-report.xlsx", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_bytes_and_all_document_properties_contain_no_paths_or_pat()
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "timing-report.xlsx");
        await new ExcelTimingReportWriter(path).WriteAsync(SampleReport(), CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var leakShapes = new[] { "/home", "/tmp", "C:\\", root.Path, profile }
            .Where(shape => !string.IsNullOrEmpty(shape))
            .ToArray();

        Assert.DoesNotContain("AZDO_PAT", raw, StringComparison.Ordinal);
        foreach (var shape in leakShapes)
        {
            Assert.DoesNotContain(shape, raw, StringComparison.Ordinal);
        }

        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        // Enumerate EVERY string document property, so future ClosedXML additions are covered.
        var stringProperties = workbook.Properties
            .GetType()
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string));

        foreach (var property in stringProperties)
        {
            var text = (string?)property.GetValue(workbook.Properties) ?? string.Empty;
            Assert.DoesNotContain("AZDO_PAT", text, StringComparison.Ordinal);
            foreach (var shape in leakShapes)
            {
                Assert.DoesNotContain(shape, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Workbook_does_not_leak_paths_or_pat()
    {
        using var workbook = Open(SampleReport());

        Assert.DoesNotContain("/home", workbook.Properties.Author ?? string.Empty, StringComparison.Ordinal);

        foreach (var sheet in workbook.Worksheets)
        {
            Assert.DoesNotContain("/home", sheet.Name, StringComparison.Ordinal);
            foreach (var cell in sheet.CellsUsed())
            {
                if (cell.DataType == XLDataType.Text)
                {
                    Assert.DoesNotContain("/home", cell.GetString(), StringComparison.Ordinal);
                    Assert.DoesNotContain("AZDO_PAT", cell.GetString(), StringComparison.Ordinal);
                }
            }
        }
    }

    private static XLWorkbook Open(TimingReport report)
        => new(new MemoryStream(ExcelTimingReportWriter.BuildWorkbook(report)));

    private static BuildRun Run(
        int id,
        DateTimeOffset? queue,
        DateTimeOffset? start = null,
        DateTimeOffset? finish = null)
        => TestRuns.Create(id: id, queueTime: queue, startTime: start, finishTime: finish);

    private static TimingReport SampleReport()
        => new(SampleSummary(), SampleRuns());

    private static BuildRun[] SampleRuns()
    {
        var origin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return
        [
            Run(id: 1, queue: origin, start: origin.AddSeconds(10), finish: origin.AddSeconds(40)),
            Run(id: 2, queue: null)
        ];
    }

    private static TimingSummary SampleSummary()
        => new(
            new TimingTotals(10, 6, 2, 1, 1, 0, 3, 12.35, 45.5, 80.25),
            [
                new MonthlyTimingSummary("2024-01", new TimingTotals(6, 4, 1, 1, 0, 0, 2, 10.0, 30.0, 60.0)),
                new MonthlyTimingSummary("(unknown)", new TimingTotals(4, 2, 1, 0, 1, 0, 1, null, null, null))
            ]);

    private static TimingSummary NullAverageSummary()
        => new(
            new TimingTotals(3, 1, 1, 0, 0, 0, 0, null, null, null),
            [new MonthlyTimingSummary("2024-02", new TimingTotals(3, 1, 1, 0, 0, 0, 0, null, null, null))]);
}
