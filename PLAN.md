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

- `manifest.json` holds: query fingerprint, last committed page cursor, completed
  run ids, timestamps, last error, and status (`pending|in_progress|completed|failed`).
- Resume key = fingerprint of (org, project, time range, definition ids, outputRoot).
- Advance the checkpoint **only after** the run file is durably written.
- Checkpoint must never be ahead of the files.
- Upsert by run id; repeated pages must not duplicate data.
- Page sequentially. Never parallelize list retrieval.

## Retrieval Rules

- Fetch build list once per page; consume list fields directly.
- Per-run detail endpoint only when a required field is missing — opt-in, not default.
- Handle `429`/`5xx` with bounded exponential backoff and `Retry-After`.
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

Retrieval: multi-page token forwarding; resume starts at persisted cursor;
crash-before-commit replays without duplicates; repeated token does not loop;
empty first page is idempotent.

Persistence: page commit atomic; rollback on mid-page failure; upsert updates
mutable fields; durability across reopen; checkpoint scoped by fingerprint.

Retry: capped exponential backoff; `Retry-After` respected; transient retried,
permanent fails fast; exhaustion throws typed error; cancellation mid-backoff stops.

Summaries: durations from timestamps; missing/skewed timestamps → null, not zero;
averages ignore nulls; monthly grouping by UTC queue month with an unknown bucket.

Constraints: no real network; no `Task.Delay` in tests; no `DateTime.UtcNow` inside
pure logic; no shared database file across tests.

## Security

- Fix in the first change: `.gitignore` must exclude `build-analytics.config.json`.
- PAT via `AZDO_PAT` only; never logged or written.
- No local absolute paths in report output.

## Open Questions (resolve before implementation)

1. Retryable status set and whether `Retry-After` may exceed the backoff cap.
2. Checkpoint rule on partial page when a run cap is applied.
3. Null vs. negative policy when `finishTime < startTime`.
4. Summary scope: seconds only, or percentiles/cost too.
5. Output beyond Excel: CSV/JSON aggregates? Workbook charts? None yet.
6. Acceptable CLI breakage level.

## Handoff

| Role | Deliverable |
|---|---|
| architect | Confirm this layout; resolve open questions 1–3 |
| implementer | Execute slices 1–3 test-first on a new branch |
| tester | Own the test matrix; review the implementer's tests for gaps |
| reviewer | Critique design against SOLID and over-engineering before merge |

## Blocker

`dotnet` SDK is not installed in this environment, so nothing can be compiled or
run here. Needed before implementation starts.
