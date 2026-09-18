using System.Globalization;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;
using ClosedXML.Excel;

namespace BuildAnalytics.App.Reporting;

/// <summary>
/// Excel adapter for the timing report (ADR-76/105). Owns its destination and writes atomically
/// (temp -> flush -> rename) through <see cref="AtomicFileWriter"/>, so a failed write never
/// truncates a prior report and leaves no staged temp file behind.
/// </summary>
public sealed class ExcelTimingReportWriter : ITimingReportWriter
{
    public const string DefaultFileName = "timing-report.xlsx";

    private const string OverviewSheet = "Overview";
    private const string MonthlySheet = "Monthly";
    private const string RunsSheet = "Runs";
    private const string SecondsFormat = "0.00";

    private static readonly string[] MonthlyHeaders =
    [
        "Month",
        "Runs",
        "Succeeded",
        "Failed",
        "Partially Succeeded",
        "Canceled",
        "Not Started",
        "Wait > 5 Min",
        "Avg Queue Wait (sec)",
        "Avg Duration (sec)",
        "Avg Total (sec)"
    ];

    private static readonly string[] RunsHeaders =
    [
        "RunId",
        "DefinitionId",
        "DefinitionName",
        "BuildNumber",
        "QueueTime",
        "StartTime",
        "FinishTime",
        "Status",
        "Result",
        "Reason",
        "PoolId",
        "PoolName",
        "SourceBranch",
        "QueueWaitSeconds",
        "RunDurationSeconds",
        "TotalDurationSeconds"
    ];

    private readonly string _destinationPath;
    private readonly AtomicFileWriter _writer;

    public ExcelTimingReportWriter(string destinationPath, IFileOperations? fileOperations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        _destinationPath = Path.GetFullPath(destinationPath);
        _writer = new AtomicFileWriter(fileOperations ?? new PhysicalFileOperations());
    }

    public async Task WriteAsync(TimingReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var bytes = BuildWorkbook(report);

        try
        {
            await _writer.WriteAsync(_destinationPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // ADR-95: never leak the absolute destination/directory into the message.
            throw new ReportingWriteException(Path.GetFileName(_destinationPath), SanitizeReason(exception.Message), exception);
        }
    }

    private string SanitizeReason(string message)
    {
        var directory = Path.GetDirectoryName(_destinationPath);
        var sanitized = message;

        sanitized = sanitized.Replace(_destinationPath, Path.GetFileName(_destinationPath), StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(directory))
        {
            sanitized = sanitized.Replace(directory, string.Empty, StringComparison.Ordinal);
        }

        return sanitized;
    }

    /// <summary>In-memory workbook bytes; exposed for tests that inspect the layout without a file.</summary>
    public static byte[] BuildWorkbook(TimingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        BuildOverview(workbook, report.Summary.Overall);
        BuildMonthly(workbook, report.Summary.Months);
        BuildRuns(workbook, report.Runs);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildOverview(XLWorkbook workbook, TimingTotals totals)
    {
        var sheet = workbook.Worksheets.Add(OverviewSheet);
        sheet.Cell(1, 1).Value = "Metric";
        sheet.Cell(1, 2).Value = "Value";

        var row = 2;
        WriteCount(sheet, ref row, "Runs", totals.RunCount);
        WriteCount(sheet, ref row, "Succeeded", totals.SucceededCount);
        WriteCount(sheet, ref row, "Failed", totals.FailedCount);
        WriteCount(sheet, ref row, "Partially Succeeded", totals.PartiallySucceededCount);
        WriteCount(sheet, ref row, "Canceled", totals.CanceledCount);
        WriteCount(sheet, ref row, "Not Started", totals.NotStartedCount);
        WriteCount(sheet, ref row, "Wait > 5 Min", totals.WaitOverFiveMinutesCount);
        WriteAverage(sheet, ref row, "Avg Queue Wait (sec)", totals.AverageQueueWaitSeconds);
        WriteAverage(sheet, ref row, "Avg Duration (sec)", totals.AverageRunDurationSeconds);
        WriteAverage(sheet, ref row, "Avg Total (sec)", totals.AverageTotalDurationSeconds);
    }

    private static void BuildMonthly(XLWorkbook workbook, IReadOnlyList<MonthlyTimingSummary> months)
    {
        var sheet = workbook.Worksheets.Add(MonthlySheet);

        for (var column = 0; column < MonthlyHeaders.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = MonthlyHeaders[column];
        }

        var row = 2;
        foreach (var month in months)
        {
            sheet.Cell(row, 1).Value = month.Month;
            sheet.Cell(row, 2).Value = month.Totals.RunCount;
            sheet.Cell(row, 3).Value = month.Totals.SucceededCount;
            sheet.Cell(row, 4).Value = month.Totals.FailedCount;
            sheet.Cell(row, 5).Value = month.Totals.PartiallySucceededCount;
            sheet.Cell(row, 6).Value = month.Totals.CanceledCount;
            sheet.Cell(row, 7).Value = month.Totals.NotStartedCount;
            sheet.Cell(row, 8).Value = month.Totals.WaitOverFiveMinutesCount;
            WriteAverage(sheet, row, 9, month.Totals.AverageQueueWaitSeconds);
            WriteAverage(sheet, row, 10, month.Totals.AverageRunDurationSeconds);
            WriteAverage(sheet, row, 11, month.Totals.AverageTotalDurationSeconds);
            row++;
        }
    }

    private static void BuildRuns(XLWorkbook workbook, IReadOnlyList<BuildRun> runs)
    {
        var sheet = workbook.Worksheets.Add(RunsSheet);

        for (var column = 0; column < RunsHeaders.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = RunsHeaders[column];
        }

        // ADR-105: QueueTime ascending, nulls last, then RunId ascending - deterministic.
        var ordered = runs
            .OrderBy(run => run.QueueTime is null ? 1 : 0)
            .ThenBy(run => run.QueueTime ?? DateTimeOffset.MinValue)
            .ThenBy(run => run.Id)
            .ToArray();

        var row = 2;
        foreach (var run in ordered)
        {
            var timing = TimingCalculator.Calculate(run);

            sheet.Cell(row, 1).Value = run.Id;
            WriteOptionalInt(sheet, row, 2, run.DefinitionId);
            WriteText(sheet, row, 3, run.DefinitionName);
            WriteText(sheet, row, 4, run.BuildNumber);
            WriteTimestamp(sheet, row, 5, run.QueueTime);
            WriteTimestamp(sheet, row, 6, run.StartTime);
            WriteTimestamp(sheet, row, 7, run.FinishTime);
            WriteText(sheet, row, 8, run.Status);
            WriteText(sheet, row, 9, run.Result);
            WriteText(sheet, row, 10, run.Reason);
            WriteOptionalInt(sheet, row, 11, run.PoolId);
            WriteText(sheet, row, 12, run.PoolName);
            WriteText(sheet, row, 13, run.SourceBranch);
            WriteOptionalDouble(sheet, row, 14, timing.QueueWaitSeconds);
            WriteOptionalDouble(sheet, row, 15, timing.RunDurationSeconds);
            WriteOptionalDouble(sheet, row, 16, timing.TotalDurationSeconds);
            row++;
        }
    }

    private static void WriteCount(IXLWorksheet sheet, ref int row, string metric, int value)
    {
        sheet.Cell(row, 1).Value = metric;
        sheet.Cell(row, 2).Value = value;
        row++;
    }

    private static void WriteAverage(IXLWorksheet sheet, ref int row, string metric, double? value)
    {
        sheet.Cell(row, 1).Value = metric;
        WriteAverage(sheet, row, 2, value);
        row++;
    }

    private static void WriteAverage(IXLWorksheet sheet, int row, int column, double? value)
    {
        if (value is not { } number)
        {
            // Null average renders as a blank cell (ADR-76).
            return;
        }

        var cell = sheet.Cell(row, column);
        cell.Value = number;
        cell.Style.NumberFormat.Format = SecondsFormat;
    }

    private static void WriteText(IXLWorksheet sheet, int row, int column, string? value)
    {
        if (value is not null)
        {
            sheet.Cell(row, column).Value = value;
        }
    }

    private static void WriteOptionalInt(IXLWorksheet sheet, int row, int column, int? value)
    {
        if (value is { } number)
        {
            sheet.Cell(row, column).Value = number;
        }
    }

    private static void WriteOptionalDouble(IXLWorksheet sheet, int row, int column, double? value)
    {
        if (value is not { } number)
        {
            return;
        }

        var cell = sheet.Cell(row, column);
        cell.Value = number;
        cell.Style.NumberFormat.Format = SecondsFormat;
    }

    private static void WriteTimestamp(IXLWorksheet sheet, int row, int column, DateTimeOffset? value)
    {
        if (value is { } timestamp)
        {
            sheet.Cell(row, column).Value = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }
    }
}
