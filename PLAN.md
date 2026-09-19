# Build Analytics — Design

A focused .NET tool that retrieves Azure DevOps build-run data and writes an Excel
timing report. This document describes the design as it currently stands, so the
application can be rebuilt from scratch by following it.

## Goal

- Summarize build timing data (and support later visualization).
- Retrieve **only what is necessary**.
- Keep analysis simple and entirely local.
- Follow SOLID and TDD.

## Decisions

1. **.NET 10, xUnit.** Three projects: `BuildAnalytics.Core` (pure domain and
   ports), `BuildAnalytics.App` (adapters + CLI + composition root),
   `BuildAnalytics.Tests`. `TreatWarningsAsErrors` on all three.
2. **Raw files are the source of truth.** All runs live in one append-only log;
   reporting reads that log and never touches the network.
3. **Clear-first and stateless.** Each invocation deletes the previous log and
   report, lists the whole history, appends every page, then writes the report.
   There is no manifest, lock, resume, incremental mode or stored identity.
4. **One command.** Retrieval and reporting are a single invocation; there are no
   subcommands and no report-only mode.
5. **Fetch the list, not each build.** The build-list payload already contains
   every field the report uses, so it is the only data source; the tool never
   fetches an individual build document.
6. **Credentials are environmental.** `AZDO_PAT` only — never a CLI flag, never
   persisted, never logged.

## Layout

```
src/BuildAnalytics.Core   # models, timing, query policy, ports, typed errors
src/BuildAnalytics.App    # Azure DevOps client, file stores, Excel writer, CLI
tests/BuildAnalytics.Tests
```

`Core` is pure: no file or network IO, no ambient clock, no package or project
references. A test enforces this (assembly references, `.csproj` contents, and a
lexical scan of the sources).

## Runtime lifecycle

```
build-analytics --org <url> --project <name> --output-root <dir> [options]

1. parse options (and optional config); require AZDO_PAT
2. clear: delete <output-root>/runs.jsonl and the previous report file
3. list every page; append each page to runs.jsonl, then report the page
4. print "Generating report..."; read the log, summarize, write the workbook
5. exit 0 (ok) / 1 (failure) / 2 (usage) / 130 (Ctrl+C)
```

Nothing else in the output directory is touched, and the clear happens before
the first list call and only once the PAT is present.

## On-disk format

```
<output-root>/
  runs.jsonl        # one run per line
  timing-report.xlsx
```

`runs.jsonl` is one **compact JSON object per line** (camelCase, string enums):

```
schemaVersion, fetchedAt, id, definitionId, definitionName,
buildNumber, queueTime, startTime, finishTime, status, result, reason,
poolId, poolName, sourceBranch
```

ADO fields outside this contract (nested `definition`/`queue`, `requestedFor`,
`requestedBy`, `sourceVersion`, `tags`, `uri`, `webUrl`, `keepForever`) are
dropped. Durations are not stored; they are derived by the timing calculator.

Write and read rules:

- **Append** one line per run, flushed durably before the page is announced; a
  leading newline is written if the file does not end with one (recovering from a
  crash-truncated tail).
- **Read**: a later line for the same `id` supersedes an earlier one (last line
  wins). An unparseable line is skipped and counted; a final line without its
  newline is tolerated as a crash fragment.
- The `id` inside a line is authoritative; there is no per-run file or directory.

## Retrieval

- `IBuildSource` is page-oriented: `ListAsync(query, continuationToken)` returns a
  page of runs plus the next token.
- One GET per list call. `api-version`, `$top` (page size 1000, trimmed by the
  remaining budget) and `queryOrder=queueTimeDescending`; the next token is read
  from `x-ms-continuationtoken` and sent back as a query parameter.
- **Budget**: `$top = min(pageSize, remaining)`. A budget of `0` stops before the
  first call; a negative budget is rejected.
- **Retry**: 5 attempts with waits 1, 2, 4, 8s for `408, 429, 500, 502, 503, 504`
  and for connection/IO/socket failures and request timeouts. A **cancelled
  caller token is rethrown, never retried**.
- **`Retry-After`**: accepted as delta-seconds or an HTTP date; values up to 60s
  are honoured, longer ones stop the run.
- **Continuation tokens**: an invalid or repeated token restarts the listing from
  the beginning exactly once; a second occurrence fails the run.
- Error text carries the HTTP status, a relative path and a correlation id — never
  the raw response body, the PAT, or an absolute local path.

## Reporting

Reads `runs.jsonl`, aggregates in `Core`, writes one workbook. No network.

Sheets:

- **`Runs`** — the raw data as an Excel **Table** (`RunsTable`): the 16 contract
  columns above plus computed `QueueWaitSeconds`, `RunDurationSeconds`,
  `TotalDurationSeconds`, then **hidden helper columns** `IsSucceeded`,
  `IsFailed`, `IsPartiallySucceeded`, `IsCanceled`, `IsNotStarted`,
  `WaitOver5Min`, `Month`, `Visible` (each 1/0 or a key, feeding the formulas).
  Timestamps are typed UTC date cells; rows are ordered by `QueueTime` (nulls
  last) then `id`; autofilter is on.
- **`Overview`** — `Metric`/`Value`, filter-aware: `Runs` counts visible rows and
  the other metrics are `SUBTOTAL` over the table (counts over the 1/0 helpers,
  `AVERAGE` for the three duration averages).
- **`Monthly`** — `Month` plus the same metrics per UTC month, real months
  ascending with `(unknown)` last. Filter-aware via `SUMIFS`/`AVERAGEIFS` that
  carry an explicit `RunsTable[Visible] = 1` criterion; the `(unknown)` row uses a
  blank criterion (its `Month` helper is blank).

Notes:

- The distinction that matters: `COUNTIFS`/`SUMIFS` alone ignore a filter, so
  every aggregation must be `SUBTOTAL` or carry the `Visible` criterion.
- The workbook is set to full-calculation on load, because the summary values are
  formulas that Excel evaluates when opened; tests assert the formulas, the table
  structure and the hidden columns.
- An empty log produces a valid zero-filled workbook.

## CLI

One command:

```
build-analytics --org <url> --project <name> --output-root <path>
    [--out <file.xlsx>] [--max-runs <n>]
    [--api-version <v>] [--quiet] [--config <path>]
build-analytics --help
```

- `--out` defaults to `<output-root>/timing-report.xlsx`; `--api-version` defaults
  to `7.1`; `--max-runs` defaults to unbounded.
- Progress goes to **stderr** (one line per page, then `Generating report...`);
  the report path goes to stdout. `--quiet` suppresses progress but never errors.
- `help`, `--help`, `-h` and no arguments print usage and exit `0`.
- Exit codes: `0` success, `1` runtime failure (including a stopped run), `2` usage
  error, `130` cancelled.
- Parsing is pure: it performs no IO and never exits the process; only `Main` maps
  a result to an exit code.

### Config file

Optional JSON at `build-analytics.config.json` (working directory) or
`--config <path>`. Precedence is **CLI → config → built-in default**; there is no
environment tier for these values.

Keys: `org`, `project`, `outputRoot`, `apiVersion`, `maxRuns`, `quiet`,
`out`. A `pat` key is a **usage error naming `AZDO_PAT`** and is never read;
unknown keys are ignored. A missing default file is fine; a missing file named by
`--config` is an error.

## Security

- `AZDO_PAT` is the only credential source, read through an injectable seam so
  tests never touch the process environment. Authentication is HTTP Basic with an
  empty username and the PAT as the password.
- No PAT or absolute local path appears in the log, the report, error text or any
  other artifact.

## Testing

- xUnit, one row per requirement. No real network: a scripted `HttpMessageHandler`
  drives the adapter, and each test gets its own temporary output root.
- Deterministic: injected clock and delay seam, no `Task.Delay`/sleeps, no ambient
  time inside pure logic.
- Areas covered: timing calculations and monthly rollup; the run log (append,
  last-wins dedupe, malformed tolerance, truncation, durability); the tree
  clearer; adapter paging, retry, `Retry-After`, token restart and budget; the pipeline (paging, append-before-progress, restart replay, progress);
  reporting (sheets, filter-aware formulas, typed dates, zero-run workbook); CLI
  parsing, exit codes, progress/quiet and the PAT gate.

## Accepted limitations

- **No lock.** Two runs against one output root will interleave (one clearing while
  the other appends). Single-user use only.
- **Every run re-retrieves the whole history** by design; the list payload is the
  data, and the volume does not justify incremental machinery.
- **Excel's runtime evaluation is not covered by tests** — the filter-aware
  formulas are asserted structurally, not executed.
- **No live Azure DevOps verification** in tests; all adapter behaviour is verified
  against scripted responses.
