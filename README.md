# build-analytics

Retrieve Azure DevOps build-run timing data and produce a local Excel summary.

The tool retrieves the build **list** only, writes it to a single append-only log,
and then generates the report — all in one command. Every invocation is stateless:
the previous log and report are cleared first, so each run re-retrieves the full
history.

## Requirements

- .NET SDK 10
- An Azure DevOps personal access token with **build read** scope

## Build and test

```bash
dotnet build build-analytics.slnx
dotnet test  build-analytics.slnx
```

## Run

```bash
export AZDO_PAT='<token>'

dotnet run --project src/BuildAnalytics.App -- \
  --org https://dev.azure.com/<organization> \
  --project <project> \
  --output-root ./analytics
```

There are no sub-commands. `--help`, `-h`, or `help` prints usage and exits `0`;
running with no arguments also prints usage.

Options:

| Flag | Required | Notes |
|---|---|---|
| `--org <url>` | yes | Organization URL, e.g. `https://dev.azure.com/acme` |
| `--project <name>` | yes | Project name (case-sensitive) |
| `--output-root <path>` | yes | Created if absent; its `runs.jsonl` is cleared at the start of every run |
| `--out <file.xlsx>` | no | Report path. Defaults to `<output-root>/timing-report.xlsx`; cleared at the start of every run |
| `--max-runs <n>` | no | Retrieval budget. `0` stops immediately; omit for unlimited |
| `--api-version <v>` | no | Default `7.1` |
| `--quiet` | no | Suppress progress output |
| `--config <path>` | no | Config file to load (see below) |

**Clear-first:** before retrieval the tool deletes `<output-root>/runs.jsonl` and
the `--out` report if they exist. Nothing else in the output directory is touched.
A run that stops halfway leaves a partial log that the next run clears.

**Retrieve then report:** the tool lists every page from the beginning, appends
each page to `runs.jsonl`, then reads the log back, summarises it, and writes the
workbook.

**Progress:** progress goes to stderr — one line per page once that page's
runs are durable (`Retrieving page 3 - 1,000 builds`) and a final
`Generating report...`. The completion line reports runs read and malformed log
lines skipped. `--quiet` suppresses it.

## Report

The workbook is written to `--out` (default `<output-root>/timing-report.xlsx`).
It has three sheets:

- **Overview** — Runs, Succeeded, Failed, Partially Succeeded, Canceled, Not Started, Wait > 5 Min, and average queue-wait / run / total duration (seconds). It is **filter-aware**: the values are `SUBTOTAL` formulas over the `Runs` table, so filtering `Runs` updates them (in Excel).
- **Monthly** — the same columns per UTC month, `(unknown)` last. It is **filter-aware too**: each cell is a `SUMIFS`/`AVERAGEIFS` over the `Runs` table carrying an explicit `Visible = 1` criterion, so filtering `Runs` updates the monthly rows as well.
- **Runs** — an Excel Table with one row per stored run and its raw fields plus computed durations, ordered by `QueueTime` ascending (nulls last) then `RunId`. Columns: `RunId`, `DefinitionId`, `DefinitionName`, `BuildNumber`, `QueueTime`, `StartTime`, `FinishTime`, `Status`, `Result`, `Reason`, `PoolId`, `PoolName`, `SourceBranch`, `QueueWaitSeconds`, `RunDurationSeconds`, `TotalDurationSeconds`. Hidden helper columns (`IsSucceeded`, `IsFailed`, `IsPartiallySucceeded`, `IsCanceled`, `IsNotStarted`, `WaitOver5Min`, `Month`, `Visible`) feed the formulas.

Because `Runs` is a proper Excel Table, you can select it and insert your own
PivotTable or PivotChart natively (`Insert > PivotTable`).

All durations are seconds. Blank cells mean "not available" (not zero). Malformed
log lines are skipped and do not appear on the `Runs` sheet. A run with no builds
still gets a header-only `Runs` table and a valid workbook. The workbook is marked
to full-calculate on load, so Excel evaluates the `Overview` formulas when opened.

## Configuration file

Any of the settings above can also come from an optional JSON file. The tool
reads `build-analytics.config.json` from the working directory, or a path given
with `--config <path>`. A missing default file is fine; a missing file named via
`--config` is a usage error (exit `2`).

```json
{
  "org": "https://dev.azure.com/acme",
  "project": "my-project",
  "outputRoot": "./analytics",
  "apiVersion": "7.1",
  "maxRuns": 1000,
  "quiet": false,
  "out": "./analytics/timing-report.xlsx"
}
```

Precedence is **CLI → config file → built-in default**. Unknown keys are ignored.
There is deliberately **no environment tier** and **no `pat` key**: a `pat` key is
a hard usage error naming `AZDO_PAT`.

## Files on disk

```
<output-root>/
  runs.jsonl                    # append-only raw runs (one compact JSON object per line)
  timing-report.xlsx            # report output (default location)
```

There is **no manifest and no lock file**: the tool keeps no cross-run state. Each
invocation clears `runs.jsonl` and the report before starting, so there is no
resume or incremental mode — every run re-lists the full history.

## Credentials

`AZDO_PAT` is read from the environment. There is deliberately:

- **no `--pat` flag** (it would leak into shell history and process listings)
- **no `pat` key in the config file** (it is a hard error)
- **no PAT stored on disk** — it never appears in `runs.jsonl`, the workbook,
  logs, or error messages

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (retrieval and report both completed) |
| `1` | Runtime failure (including a stopped retrieval) |
| `2` | Usage error (unknown flag, missing or invalid value) |
| `130` | Cancelled with Ctrl+C |

## Design

See [`PLAN.md`](PLAN.md) for the architecture and the design decisions behind the
retrieval, storage, reporting, and CLI behaviour.
