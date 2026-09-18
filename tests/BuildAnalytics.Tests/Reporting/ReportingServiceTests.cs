using BuildAnalytics.App.Reporting;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Tests.Doubles;
using BuildAnalytics.Tests.Retrieval;
using BuildAnalytics.Tests.Storage;
using ClosedXML.Excel;
using System.Text.Json;

namespace BuildAnalytics.Tests.Reporting;

public sealed class ReportingServiceTests
{
    private static readonly DateTimeOffset Queue = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Missing_manifest_throws_reporting_error()
    {
        var (service, _, _, _) = Create();

        var exception = await Assert.ThrowsAsync<ReportingErrorException>(() => service.GenerateAsync(CancellationToken.None));

        Assert.Null(exception.Status);
    }

    [Theory]
    [InlineData(ManifestStatus.Pending)]
    [InlineData(ManifestStatus.InProgress)]
    [InlineData(ManifestStatus.Paused)]
    [InlineData(ManifestStatus.Failed)]
    public async Task Non_completed_manifest_throws_reporting_error(ManifestStatus status)
    {
        var (service, manifests, _, _) = Create();
        manifests.Current = ManifestWith(status);

        var exception = await Assert.ThrowsAsync<ReportingErrorException>(() => service.GenerateAsync(CancellationToken.None));

        Assert.Equal(status, exception.Status);
    }

    [Fact]
    public async Task Completed_empty_root_writes_a_valid_zero_filled_report()
    {
        var (service, manifests, runs, writer) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed);

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(0, result.RunsRead);
        Assert.Equal(0, result.CorruptSkipped);
        Assert.Equal(0, result.Report.Summary.Overall.RunCount);
        Assert.Empty(result.Report.Summary.Months);
        Assert.NotNull(writer.Report!.Summary);
        Assert.Equal(result.Report.Summary, writer.Report!.Summary);
    }

    [Fact]
    public async Task Completed_zero_run_root_writes_a_valid_real_workbook()
    {
        using var root = new TempOutputRoot();
        await File.WriteAllBytesAsync(
            Path.Combine(root.Path, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(ManifestWith(ManifestStatus.Completed), BuildAnalyticsJson.Options));

        var outputPath = Path.Combine(root.Path, "report.xlsx");
        using var manifestReader = FileManifestStore.OpenReadOnly(root.Path);
        var service = new ReportingService(
            manifestReader,
            new FileRunStore(root.Path, new PhysicalFileOperations()),
            new ExcelTimingReportWriter(outputPath));

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(0, result.RunsRead);
        using var workbook = new XLWorkbook(outputPath);
        var overview = workbook.Worksheet("Overview");
        // ADR-107: Overview is formula-driven now; assert structure, not computed values.
        Assert.Equal("IFERROR(SUBTOTAL(103,RunsTable[RunId]),0)", overview.Cell(2, 2).FormulaA1);
        Assert.Equal("IFERROR(SUBTOTAL(101,RunsTable[QueueWaitSeconds]),\"\")", overview.Cell(9, 2).FormulaA1);
        Assert.Equal("IFERROR(SUBTOTAL(101,RunsTable[RunDurationSeconds]),\"\")", overview.Cell(10, 2).FormulaA1);
        Assert.Equal("IFERROR(SUBTOTAL(101,RunsTable[TotalDurationSeconds]),\"\")", overview.Cell(11, 2).FormulaA1);

        var monthly = workbook.Worksheet("Monthly");
        Assert.Equal("Month", monthly.Cell(1, 1).GetString());
        Assert.True(monthly.Cell(2, 1).IsEmpty());

        var runsSheet = workbook.Worksheet("Runs");
        Assert.Equal("RunId", runsSheet.Cell(1, 1).GetString());
        Assert.Equal("TotalDurationSeconds", runsSheet.Cell(1, 16).GetString());
        Assert.Equal("Month", runsSheet.Cell(1, 23).GetString());
        Assert.True(runsSheet.Cell(2, 1).IsEmpty());
        // ClosedXML supports header-only ListObjects, so even a zero-run root gets a real table.
        Assert.Equal("RunsTable", runsSheet.Table("RunsTable").Name);
    }

    [Fact]
    public async Task Aggregates_present_runs_and_reports_counts()
    {
        var (service, manifests, runs, writer) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed);
        runs.Put(Run(1, "succeeded"));
        runs.Put(Run(2, "succeeded"));
        runs.Put(Run(3, "failed"));

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(3, result.RunsRead);
        Assert.Equal(0, result.CorruptSkipped);
        Assert.Equal(3, result.Report.Summary.Overall.RunCount);
        Assert.Equal(2, result.Report.Summary.Overall.SucceededCount);
        Assert.Equal(1, result.Report.Summary.Overall.FailedCount);
        Assert.Equal(result.Report.Summary, writer.Report!.Summary);
    }

    [Fact]
    public async Task Corrupt_run_is_skipped_and_counted_without_aborting()
    {
        var (service, manifests, runs, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed);
        runs.Put(Run(1, "succeeded"));
        runs.Seed(2);
        runs.Unreadable.Add(2);

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(1, result.RunsRead);
        Assert.Equal(1, result.CorruptSkipped);
        Assert.Equal(1, result.Report.Summary.Overall.RunCount);
        Assert.Single(result.Report.Runs);
        Assert.Equal(1, result.Report.Runs[0].Id);
    }

    [Fact]
    public async Task Unsupported_schema_run_aborts_instead_of_skipping()
    {
        var (service, manifests, runs, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed);
        runs.Seed(1);
        runs.StaleSchema.Add(1);

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => service.GenerateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Listed_but_absent_run_is_skipped_silently()
    {
        var (service, manifests, runs, _) = Create();
        manifests.Current = ManifestWith(ManifestStatus.Completed);
        runs.Put(Run(1, "succeeded"));
        runs.ListedButAbsent.Add(99);

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(1, result.RunsRead);
        Assert.Equal(0, result.CorruptSkipped);
        Assert.Equal(1, result.Report.Summary.Overall.RunCount);
    }

    [Fact]
    public async Task GenerateAsync_does_not_mutate_the_output_root()
    {
        using var root = new TempOutputRoot();

        await File.WriteAllBytesAsync(
            Path.Combine(root.Path, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(ManifestWith(ManifestStatus.Completed), BuildAnalyticsJson.Options));

        var runDirectory = Path.Combine(root.Path, "runs", "1");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(runDirectory, "run.json"),
            JsonSerializer.SerializeToUtf8Bytes(Run(1, "succeeded"), BuildAnalyticsJson.Options));

        var beforePaths = Directory
            .GetFileSystemEntries(root.Path, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var beforeBytes = beforePaths
            .Where(File.Exists)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

        using var manifestReader = FileManifestStore.OpenReadOnly(root.Path);
        var service = new ReportingService(
            manifestReader,
            new FileRunStore(root.Path, new PhysicalFileOperations()),
            new InMemoryTimingReportWriter());

        var result = await service.GenerateAsync(CancellationToken.None);

        Assert.Equal(1, result.RunsRead);
        Assert.False(File.Exists(Path.Combine(root.Path, "manifest.lock")));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));

        var afterPaths = Directory
            .GetFileSystemEntries(root.Path, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(beforePaths, afterPaths);

        foreach (var path in beforeBytes.Keys)
        {
            Assert.Equal(beforeBytes[path], await File.ReadAllBytesAsync(path, CancellationToken.None));
        }
    }

    [Fact]
    public void Reporting_ctor_has_no_network_dependency()
    {
        var parameters = typeof(ReportingService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(parameters, type => typeof(HttpClient).IsAssignableFrom(type));
        Assert.DoesNotContain(parameters, type => type == typeof(IBuildSource));
    }

    private static BuildRun Run(int id, string result)
        => TestRuns.Create(
            id: id,
            result: result,
            queueTime: Queue,
            startTime: Queue.AddMinutes(1),
            finishTime: Queue.AddMinutes(2));

    private static Manifest ManifestWith(ManifestStatus status)
        => new(
            Manifest.CurrentSchemaVersion,
            "fp",
            status,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            []);

    private static (ReportingService Service, RecordingManifestStore Manifests, RecordingRunStore Runs, InMemoryTimingReportWriter Writer) Create()
    {
        var manifests = new RecordingManifestStore();
        var runs = new RecordingRunStore();
        var writer = new InMemoryTimingReportWriter();
        return (new ReportingService(manifests, runs, writer), manifests, runs, writer);
    }
}
