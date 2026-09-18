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
| `--from <iso>` / `--to <iso>` | no | ISO-8601 bounds on queue time |
| `--definition-id <id>` | no | Repeatable; also accepts comma-separated ids |
| `--definition <glob>` | no | Repeatable; `*`/`?` wildcard matched against definition **name or path** |
| `--detail list\|fill-missing` | no | Default `list`. `fill-missing` fetches per-run detail only when a list row is missing required fields |
| `--max-runs <n>` | no | Retrieval budget. `0` pauses immediately; omit for unlimited |
| `--page-size <n>` | no | Default `1000` |
| `--api-version <v>` | no | Default `7.1` (part of the retrieval's identity) |
| `--quiet` | no | Suppress progress output |

**Resuming:** if a run stops (Ctrl+C, a throttle, a failure), just run the same
command again. Progress is checkpointed per page in `manifest.json`, and
already-stored runs are not re-fetched.

## Report

```bash
dotnet run --project src/BuildAnalytics.App -- report \
  --output-root ./analytics \
  --out ./analytics/timing-report.xlsx
```

`--out` is optional and defaults to `<output-root>/timing-report.xlsx`. Reporting
requires a **completed** retrieval in that root; otherwise it exits non-zero.

The workbook has two sheets:

- **Overview** — Runs, Succeeded, Failed, Partially Succeeded, Canceled, Not Started, Wait > 5 Min, and average queue-wait / run / total duration (seconds)
- **Monthly** — the same columns per UTC month, `(unknown)` last

All durations are seconds. Blank cells mean "not available" (not zero).

## Files on disk

```
<output-root>/
  manifest.json                 # retrieval progress (schemaVersion, fingerprint, status, cursor)
  manifest.lock                 # exclusive lock held by a retrieve
  runs/<runId>/run.json         # one raw build run per id
  timing-report.xlsx            # report output (default location)
```

A retrieval is bound to the query that created it (`org`, `project`, time range,
resolved definition ids, detail policy, API version). Pointing a **different**
query at the same output root is an error — use a new root.

## Credentials

`AZDO_PAT` is read from the environment. There is deliberately:

- **no `--pat` flag** (it would leak into shell history and process listings)
- **no config file** and no `--config`
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
