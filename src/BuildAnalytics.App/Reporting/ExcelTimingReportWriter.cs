using System.Globalization;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;
using ClosedXML.Excel;

namespace BuildAnalytics.App.Reporting;

/// <summary>
/// Excel adapter for the timing report (ADR-76/105/107). Owns its destination and writes atomically
/// (temp -> flush -> rename) through <see cref="AtomicFileWriter"/>, so a failed write never
/// truncates a prior report and leaves no staged temp file behind.
///
/// The raw <c>Runs</c> sheet is an Excel Table; <c>Overview</c> uses filter-aware
/// <c>SUBTOTAL(101-111)</c> formulas over it (never COUNTIFS/SUMIFS), <c>Monthly</c> is static,
/// and a <c>Pivot</c> sheet slices the same table.
/// </summary>
public sealed class ExcelTimingReportWriter : ITimingReportWriter
{
    public const string DefaultFileName = "timing-report.xlsx";

    private const string OverviewSheet = "Overview";
    private const string MonthlySheet = "Monthly";
    private const string RunsSheet = "Runs";
    private const string PivotSheet = "Pivot";
    private const string RunsTableName = "RunsTable";
    private const string PivotTableName = "RunsPivot";
    private const string SecondsFormat = "0.00";
    private const string TimestampFormat = "yyyy-mm-dd hh:mm:ss";

    private const string MonthlyNote =
        "Monthly totals always cover the full run set (they do not follow the Runs filter). Use the Pivot sheet to slice interactively.";

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

    /// <summary>Hidden helper columns appended after <see cref="RunsHeaders"/>; names feed structured references.</summary>
    private static readonly string[] HelperHeaders =
    [
        "IsSucceeded",
        "IsFailed",
        "IsPartiallySucceeded",
        "IsCanceled",
        "IsNotStarted",
        "WaitOver5Min",
        "Month"
    ];

    private static readonly string[] RunsTableHeaders = [.. RunsHeaders, .. HelperHeaders];

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

        // ADR-107: Excel evaluates the SUBTOTAL formulas on open.
        workbook.FullCalculationOnLoad = true;

        BuildOverview(workbook);

        // ADR-105: Monthly stays static (from the summary).
        BuildMonthly(workbook, report.Summary.Months);

        var runsTable = BuildRuns(workbook, report.Runs);
        BuildPivot(workbook, runsTable);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// ADR-107: Overview is filter-aware. SUBTOTAL(101-111) ignores autofilter-hidden rows;
    /// COUNTIFS/SUMIFS would not, so they are deliberately not used.
    /// </summary>
    private static void BuildOverview(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.Add(OverviewSheet);
        sheet.Cell(1, 1).Value = "Metric";
        sheet.Cell(1, 2).Value = "Value";

        var row = 2;
        WriteFormula(sheet, ref row, "Runs", $"=IFERROR(SUBTOTAL(103,{RunsTableName}[RunId]),0)");
        WriteFormula(sheet, ref row, "Succeeded", $"=SUBTOTAL(109,{RunsTableName}[IsSucceeded])");
        WriteFormula(sheet, ref row, "Failed", $"=SUBTOTAL(109,{RunsTableName}[IsFailed])");
        WriteFormula(sheet, ref row, "Partially Succeeded", $"=SUBTOTAL(109,{RunsTableName}[IsPartiallySucceeded])");
        WriteFormula(sheet, ref row, "Canceled", $"=SUBTOTAL(109,{RunsTableName}[IsCanceled])");
        WriteFormula(sheet, ref row, "Not Started", $"=SUBTOTAL(109,{RunsTableName}[IsNotStarted])");
        WriteFormula(sheet, ref row, "Wait > 5 Min", $"=SUBTOTAL(109,{RunsTableName}[WaitOver5Min])");
        WriteFormula(sheet, ref row, "Avg Queue Wait (sec)", $"=IFERROR(SUBTOTAL(101,{RunsTableName}[QueueWaitSeconds]),\"\")", SecondsFormat);
        WriteFormula(sheet, ref row, "Avg Duration (sec)", $"=IFERROR(SUBTOTAL(101,{RunsTableName}[RunDurationSeconds]),\"\")", SecondsFormat);
        WriteFormula(sheet, ref row, "Avg Total (sec)", $"=IFERROR(SUBTOTAL(101,{RunsTableName}[TotalDurationSeconds]),\"\")", SecondsFormat);
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

        // ADR-107: Monthly is deliberately static; one note two rows below the last data row.
        sheet.Cell(1 + months.Count + 2, 1).Value = MonthlyNote;
    }

    private static IXLTable BuildRuns(XLWorkbook workbook, IReadOnlyList<BuildRun> runs)
    {
        var sheet = workbook.Worksheets.Add(RunsSheet);

        for (var column = 0; column < RunsTableHeaders.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = RunsTableHeaders[column];
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

            // ADR-107 helper columns (1/0) so SUBTOTAL can count over the table.
            sheet.Cell(row, 17).Value = Matches(run.Result, "succeeded") ? 1 : 0;
            sheet.Cell(row, 18).Value = Matches(run.Result, "failed") ? 1 : 0;
            sheet.Cell(row, 19).Value = Matches(run.Result, "partiallySucceeded") ? 1 : 0;
            sheet.Cell(row, 20).Value = Matches(run.Result, "canceled") ? 1 : 0;
            sheet.Cell(row, 21).Value = Matches(run.Status, "notStarted") ? 1 : 0;
            sheet.Cell(row, 22).Value =
                timing.QueueWaitSeconds is { } wait && wait > TimingCalculator.WaitOverFiveMinutesThresholdSeconds ? 1 : 0;

            // Month feeds the Pivot; blank when the queue time is absent.
            if (run.QueueTime is { } queueTime)
            {
                sheet.Cell(row, 23).Value = queueTime.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            }

            row++;
        }

        // ADR-107: header-only tables are valid in ClosedXML, so a zero-run root still gets a
        // real ListObject and the structured-reference formulas stay resolvable.
        var table = sheet.Range(1, 1, row - 1, RunsTableHeaders.Length).CreateTable(RunsTableName);
        table.ShowAutoFilter = true;

        for (var column = RunsHeaders.Length + 1; column <= RunsTableHeaders.Length; column++)
        {
            sheet.Column(column).Hide();
        }

        return table;
    }

    private static void BuildPivot(XLWorkbook workbook, IXLTable runsTable)
    {
        var sheet = workbook.Worksheets.Add(PivotSheet);
        var pivot = sheet.PivotTables.Add(PivotTableName, sheet.Cell("A1"), runsTable);

        pivot.RowLabels.Add("Month");
        pivot.Values.Add("RunId", "Count of RunId").SummaryFormula = XLPivotSummary.Count;
        pivot.Values.Add("IsSucceeded", "Sum of IsSucceeded").SummaryFormula = XLPivotSummary.Sum;
        pivot.Values.Add("IsFailed", "Sum of IsFailed").SummaryFormula = XLPivotSummary.Sum;
        pivot.Values.Add("WaitOver5Min", "Sum of WaitOver5Min").SummaryFormula = XLPivotSummary.Sum;
        pivot.Values.Add("QueueWaitSeconds", "Average of QueueWaitSeconds").SummaryFormula = XLPivotSummary.Average;
        pivot.PivotCache.RefreshDataOnOpen = true;
    }

    private static void WriteFormula(IXLWorksheet sheet, ref int row, string metric, string formula, string? format = null)
    {
        sheet.Cell(row, 1).Value = metric;
        var cell = sheet.Cell(row, 2);
        cell.FormulaA1 = formula;
        if (format is not null)
        {
            cell.Style.NumberFormat.Format = format;
        }

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
        if (value is not { } timestamp)
        {
            return;
        }

        // ADR-105: raw-data columns must be typed date cells so sort/filter/pivot work.
        var cell = sheet.Cell(row, column);
        cell.Value = timestamp.UtcDateTime;
        cell.Style.NumberFormat.Format = TimestampFormat;
    }

    /// <summary>Mirrors the case-insensitive status/result matching used by the timing rollup.</summary>
    private static bool Matches(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
