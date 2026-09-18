# build-analytics

Retrieve Azure DevOps build-run timing data and produce a local Excel summary.

The tool retrieves the build **list** only (no per-run detail calls unless you ask
for them), stores what it needs as plain files, and can be safely stopped and
resumed. Reporting runs entirely against the local files — it never touches the
network.

## Requirements

- .NET SDK 10
- An Azure DevOps personal access token with **build read** scope

## Build and test

```bash
dotnet build build-analytics.slnx
dotnet test  build-analytics.slnx
```

## Retrieve

```bash
export AZDO_PAT='<token>'

dotnet run --project src/BuildAnalytics.App -- retrieve \
  --org https://dev.azure.com/<organization> \
  --project <project> \
  --output-root ./analytics
```

Options:

| Flag | Required | Notes |
|---|---|---|
| `--org <url>` | yes | Organization URL, e.g. `https://dev.azure.com/acme` |
| `--project <name>` | yes | Project name (case-sensitive) |
| `--output-root <path>` | yes | Created if absent; reuse it to resume |
| `--detail list\|fill-missing` | no | Default `list`. `fill-missing` fetches per-run detail only when a list row is missing required fields |
| `--max-runs <n>` | no | Retrieval budget. `0` pauses immediately; omit for unlimited |
| `--api-version <v>` | no | Default `7.1` (part of the retrieval's identity) |
| `--quiet` | no | Suppress progress output |
| `--config <path>` | no | Config file to load (see below) |

**Resuming and refreshing:** re-running the same command picks up builds that
appeared since the last run. A run that stops (Ctrl+C, a throttle, a failure) is
checkpointed per page in `manifest.json`; a completed root is re-listed from the
top, already-stored runs are not re-fetched, and paging stops as soon as a page
adds no new runs. A no-new-builds run costs a single list call.

**Progress:** retrieval writes coarse progress to stderr — a start line, one
line per page, and a percentage every 5%. The percentage counts runs already on
disk at the start of the pass, so a resume or refresh continues rather than
restarting at 0. `--quiet` suppresses it.

## Report

```bash
dotnet run --project src/BuildAnalytics.App -- report \
  --output-root ./analytics \
  --out ./analytics/timing-report.xlsx
```

`--out` is optional and defaults to `<output-root>/timing-report.xlsx`. Reporting
requires a **completed** retrieval in that root; otherwise it exits non-zero.

The workbook has four sheets:

- **Overview** — Runs, Succeeded, Failed, Partially Succeeded, Canceled, Not Started, Wait > 5 Min, and average queue-wait / run / total duration (seconds). It is **filter-aware**: the values are `SUBTOTAL` formulas over the `Runs` table, so filtering `Runs` updates them (in Excel).
- **Monthly** — the same columns per UTC month, `(unknown)` last. These are **static** snapshots of the full run set and do **not** follow the `Runs` filter (a note on the sheet says so).
- **Runs** — an Excel Table with one row per stored run and its raw fields plus computed durations, ordered by `QueueTime` ascending (nulls last) then `RunId`. Columns: `RunId`, `DefinitionId`, `DefinitionName`, `BuildNumber`, `QueueTime`, `StartTime`, `FinishTime`, `Status`, `Result`, `Reason`, `PoolId`, `PoolName`, `SourceBranch`, `QueueWaitSeconds`, `RunDurationSeconds`, `TotalDurationSeconds`. Hidden 1/0 helper columns (`IsSucceeded`, `IsFailed`, `IsPartiallySucceeded`, `IsCanceled`, `IsNotStarted`, `WaitOver5Min`, `Month`) feed the formulas and pivot.
- **Pivot** — a PivotTable sourced from the `Runs` table (rows = `Month`, values = Count of `RunId`, sums of the status/`WaitOver5Min` flags, and the average queue wait) for interactive slicing.

All durations are seconds. Blank cells mean "not available" (not zero). Corrupt
run files are skipped and do not appear on the `Runs` sheet. A completed root
with no runs still gets a header-only `Runs` table and a valid workbook. The
workbook is marked to full-calculate on load, so Excel evaluates the `Overview`
formulas when opened.

## Configuration file

Any of the settings above can also come from an optional JSON file. The tool
reads `build-analytics.config.json` from the working directory, or a path given
with `--config <path>` for either verb. A missing default file is fine; a missing
file named via `--config` is a usage error (exit `2`).

```json
{
  "org": "https://dev.azure.com/acme",
  "project": "my-project",
  "outputRoot": "./analytics",
  "apiVersion": "7.1",
  "detail": "fill-missing",
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
  manifest.json                 # retrieval progress (schemaVersion, fingerprint, status, cursor)
  manifest.lock                 # exclusive lock held by a retrieve
  runs/<runId>/run.json         # one raw build run per id
  timing-report.xlsx            # report output (default location)
```

A retrieval is bound to the query that created it (`org`, `project`, detail
policy, API version). Pointing a **different** query at the same output root is
an error — use a new root.

## Credentials

`AZDO_PAT` is read from the environment. There is deliberately:

- **no `--pat` flag** (it would leak into shell history and process listings)
- **no `pat` key in the config file** (it is a hard error)
- **no PAT stored on disk** — it never appears in `manifest.json`, `run.json`,
  the workbook, logs, or error messages

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Completed (retrieve) or report written |
| `1` | Runtime failure, or retrieval ended `paused`/`failed` |
| `2` | Usage error (unknown command/flag, missing or invalid value) |
| `130` | Cancelled with Ctrl+C |

## Design

See [`PLAN.md`](PLAN.md) for the architecture and the numbered decisions
(ADRs) behind the retrieval, storage, reporting, and CLI behaviour.
