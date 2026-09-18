# Build Analytics — Rewrite Plan

Clean-slate .NET rewrite. Existing repo is reference-only; it is not modified.

## Goal

Summarize Azure DevOps build timing data (with possible later visualizations) while:

- retrieving only the data required
- resuming retrieval safely after failure
- keeping analysis simple and local
- following SOLID and TDD

## Confirmed Decisions

- Clean-slate rewrite, not a refactor.
- .NET 10, xUnit.
- **Raw files are the source of truth.**
- A small checkpoint/manifest store tracks retrieval progress only.
- Analysis/reporting performs no network access and reads local raw files.
- Credentials come from environment variables; never persisted.

## Target Layout

Keep it small — three projects:

```
BuildAnalytics.Core   # models, query/options contracts, resume state, pure analysis
BuildAnalytics.App    # CLI + composition root + ADO/filesystem/report adapters
BuildAnalytics.Tests  # xUnit
```

Split infrastructure into its own assembly only if testability forces it.

## Data Model (raw-files-primary)

```
<outputRoot>/
  manifest.json                 # retrieval progress + query fingerprint
  runs/<runId>__<name>/run.json # raw build payload; atomic write
```

- `manifest.json` holds: `schemaVersion`, query fingerprint, last committed page
  cursor, completed run ids, timestamps, last error, and status
  (`pending|in_progress|completed|failed`).
- `schemaVersion` is `1`. A newer on-disk version is a typed failure; the tool
  refuses to proceed rather than guess.
- Corrupted/truncated manifest: quarantine to `manifest.corrupt-<ts>.json`, scan
  `runs/` for existing ids, restart list pagination from `cursor=null`, and
  upsert idempotently. Only list calls are repeated, never detail calls.
- Resume key = fingerprint of (org, project, time range, definition ids, outputRoot).
- Advance the checkpoint **only after** the run file is durably written.
- Checkpoint must never be ahead of the files.
- Upsert by run id; repeated pages must not duplicate data.
- Page sequentially. Never parallelize list retrieval.

## Retrieval Rules

- Fetch build list once per page; consume list fields directly.
- Per-run detail endpoint only when a required field is missing — opt-in, not default.
- Retry only `408, 429, 500, 502, 503, 504` plus connection/timeout exceptions.
  Max 4 attempts; exponential backoff 1→2→4→8s capped at 30s; jitter is seeded
  and deterministic.
- Honor `Retry-After`, but abort with a typed error if it exceeds 60s — resuming
  later is cheaper than blocking, and the checkpoint preserves progress.
- `maxRuns` is applied at **page boundaries only**; whole pages are committed and
  retrieval never stops mid-page. It is a safety valve, so slight overshoot is fine.
- Stream output; one page in memory at a time.
- Wire `CancellationToken` from Ctrl+C through the whole pipeline.

## TDD Slices

1. **Contracts + domain (pure).** Timing metrics and monthly rollup.
2. **Resume/manifest store.** Atomicity, restart, idempotent upsert.
3. **ADO source adapter.** Paging, continuation forwarding, retries, no per-run overfetch.
4. **Pipeline.** list → select missing → fetch → write → checkpoint.
5. **Reporting.** Summary from local raw files; Excel writer as one adapter.
6. **Cleanup.** Simplify CLI/options; deprecate legacy flags after parity.

Each slice: failing test → minimal implementation → refactor → full suite green.

## Test Matrix (high value)

Grouped, with the raw-files-primary semantics applied. Source of truth: tester's
re-mapped A/B/C/D matrix plus group E.

**A — resumable retrieval.** Sequential multi-page token forwarding; resume from
manifest cursor; crash-before-commit replay without duplicates; repeated token
bounded; empty first page idempotent; **no per-run overfetch** (0 detail calls
when list fields suffice); Ctrl+C cancellation mid-pipeline; one page in memory.

**B — manifest + raw files.** Atomic `run.json` write (temp + rename);
checkpoint advanced only after durable file write; checkpoint never ahead of
files; file-level upsert by run id; durability across reopen; fingerprint
scoping; manifest status machine; no credentials in artifacts.

**C — retry/backoff.** Capped exponential schedule; jitter deterministic with
seed; `Retry-After` honored; transient retried / permanent fails fast;
exhaustion throws typed error; cancellation mid-backoff stops; retried detail
fetch stays idempotent.

**D — timing (pure).** Durations from timestamps as raw seconds; missing/skewed →
null; averages ignore nulls and round only at output; UTC monthly grouping +
`(unknown)` bucket sorted last; wait > 5 min strictly > 300 on raw seconds;
case-insensitive counts; purity guard (no ambient clock or IO).

**E — reporting/security.** Reporting never touches the network; no absolute
local paths in output; PAT never logged or written; `.gitignore` excludes
`build-analytics.config.json`.

Constraints: no real network; no `Task.Delay` in tests; no `DateTime.UtcNow`
inside pure logic; no shared mutable output root across tests.

## Security

- Fix in the first change: `.gitignore` must exclude `build-analytics.config.json`.
- PAT via `AZDO_PAT` only; never logged or written.
- No local absolute paths in report output.

## Resolved Decisions (Q1–Q8)

1. **Retry.** Retry `408, 429, 500, 502, 503, 504` and connection/timeout errors.
   4 attempts, backoff 1→2→4→8s, cap 30s. `Retry-After` honored up to 60s; beyond
   that, abort with a typed error and rely on resume.
2. **Run cap.** `maxRuns` applies at page boundaries only; commit whole pages.
3. **Skewed timestamps.** `finishTime < startTime` yields `null`, never negative.
4. **Summary scope.** Seconds only — queue wait, run duration, total.
5. **Output.** Excel only for now; CSV/JSON deferred behind `ITimingReportWriter`.
6. **CLI.** Breaking rewrite accepted; no migration note required.
7. **Corrupt manifest.** Quarantine and rebuild; replay list pages, upsert files.
8. **Schema version.** `schemaVersion: 1`; newer on disk is a typed failure.
9. **Rounding.** The domain stores **raw seconds** (full precision). Rounding to
   2dp happens only when emitting aggregates/report values. The `>300s` wait
   threshold compares raw seconds.
10. **Month ordering.** Real UTC months ascending; `(unknown)` sorts **last**.
11. **Month anchor.** Grouping anchor is `QueueTime` only; no `StartTime`
    fallback. Absent `QueueTime` → `(unknown)` bucket.
12. **Monthly metrics.** `RunCount`, `Succeeded`, `Failed`, `PartiallySucceeded`,
    `Canceled`, `NotStarted`, `WaitOverFiveMin`, and the three averages.
    "Seconds only" (Q4) means no percentiles/cost — not dropping these counts.

## Handoff

| Role | Deliverable |
|---|---|
| implementer | Execute slices 1–3 test-first on a new branch |
| tester | Own the test matrix; review the implementer's tests for gaps |
| reviewer | Critique design against SOLID and over-engineering before merge |

## Environment

- .NET SDK 10.0.401 installed.
- Baseline verified: `dotnet test build-analytics.slnx` → 15/15 pass on `da40a14`.
- Existing code builds with 3 nullable warnings; reference-only.

