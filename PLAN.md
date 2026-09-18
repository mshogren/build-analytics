# Build Analytics — Rewrite Plan

> **Canonical copy.** This file at `/workspace/build-analytics-plan/PLAN.md` is the
> single source of truth for all agents. Any `PLAN.md` inside the implementation
> repo is generated from it.

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
- A small manifest tracks retrieval progress only.
- Analysis/reporting performs no network access and reads local raw files.
- Credentials come from `AZDO_PAT` only; never persisted, never a CLI flag.

## Target Layout

Three projects. Core is **pure**; App holds every adapter.

```
BuildAnalytics.Core   # models, query/fingerprint, pure timing, PORT INTERFACES
BuildAnalytics.App    # CLI + composition root + ADO client + file stores + Excel writer
BuildAnalytics.Tests  # xUnit
```

Core references no IO: no `File`/`Directory`, no `HttpClient`, no `ClosedXML`,
no `DateTime.Now`/`UtcNow`, no `Random`/`Guid`/`Environment`. Enforced by an
architecture test scoped to **all** of Core — enforced by an assembly-reference
check plus a `.csproj` scan, with the lexical scan secondary.

Ports defined in Core (all async, all take `CancellationToken`):

```csharp
public sealed record BuildPage(IReadOnlyList<BuildRun> Runs, string? ContinuationToken);

public interface IBuildSource {
    Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken ct);
    Task<BuildRun> GetDetailAsync(BuildQuery query, int runId, CancellationToken ct); // opt-in
}
public interface IManifestStore {
    Task<Manifest?> TryReadAsync(CancellationToken ct);
    Task CommitAsync(Manifest manifest, CancellationToken ct);
}
public interface IRunStore {
    Task WriteAsync(BuildRun run, CancellationToken ct);
    Task<BuildRun?> TryReadAsync(int runId, CancellationToken ct);
    Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken ct);
}
public interface ITimingReportWriter {
    Task WriteAsync(TimingSummary summary, CancellationToken ct); // adapter owns its destination
}
public interface IDelayScheduler {
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}
```

`IBuildSource` is **page-oriented**, not `IAsyncEnumerable`: the manifest
checkpoint is a per-page cursor, so page boundaries must stay visible.
`IRunStore.ListRunIdsAsync` supports the corrupt-manifest rebuild (Q7).

Canonical types (ratified after the implementer's design landed):

- `Models.BuildRun` — flat persistence DTO, carries `FetchedAt`
- `Timing.TimingCalculator.Calculate(BuildRun) -> RunTiming` — durations
- `Timing.MonthlyTimingRollup.Summarize(IEnumerable<BuildRun>) -> TimingSummary`
- `Timing.TimingSummary(Overall, Months)`, `TimingTotals`, `MonthlyTimingSummary`
- `Query.BuildQuery` + `Query.BuildQueryFingerprint.Compute`
- `Query.DetailPolicy { ListOnly, FillMissing }`

`IDelayScheduler` is Core's only timing seam (shape-only until the retry slice),
so waits are deterministic in tests. Core has **no** `System.TimeProvider`
dependency: adapters own the clock and stamp `FetchedAt`/`CreatedAt`/`UpdatedAt`.
Resolving an HTTP-date `Retry-After` happens in the App HTTP adapter.

## Domain Model (raw-files-primary)

```
<outputRoot>/
  manifest.json          # retrieval progress + fingerprint
  manifest.lock          # exclusive single-writer lock
  runs/<runId>/run.json  # raw build payload; atomic write
```

Canonical run path is `runs/<runId>/run.json`. The old `<id>__<name>` folder is
**not** used: definition renames created duplicate folders for one run id and
broke upsert.

`run.json` field contract:

```
schemaVersion, source(list|detail), fetchedAt,
id, definitionId, definitionName, buildNumber,
queueTime, startTime, finishTime,
status, result, reason, poolId, poolName, sourceBranch
```

All of these are present in the build-list response, so the detail endpoint is a
fallback. `source` (`list`|`detail`) makes an incomplete file detectable on resume.

The schema is **flat**. ADO fields outside this contract (nested `definition`/
`queue`, `requestedFor`, `requestedBy`, `sourceVersion`, `tags`, `uri`,
`webUrl`, `keepForever`) are dropped entirely, not stored optionally.

The legacy `runs.json` artifact is **dropped**; nothing reads it.

## Fingerprint

Identity is the **effective query**:

```
org, project, minTime, maxTime,
resolvedDefinitionIds (sorted, distinct), detailPolicy, apiVersion
```

Raw `definitionIds`/`definitionNames` are stored in the manifest for human
reference but are **not** identity inputs: two invocations that resolve to the
same id set describe the same data and should resume each other.

Excluded from identity: `outputRoot` (a location), `maxRuns` (a runtime budget),
input ordering, and null-vs-empty collections (canonicalized to empty).

Canonical form: `key=value` lines joined by `\n` (`org=`, `project=`, `minTime=`,
`maxTime=`, `resolvedDefinitionIds=`, `detailPolicy=`, `apiVersion=`); a null
timestamp renders as `-`. The hash is SHA-256 lowercase hex. A golden-vector test
pins this exact format, so accidental canonicalization drift fails loudly.

A mismatch against a non-empty `runs/` is a typed error telling the operator to
use a new output root.

## Manifest & Durability

`manifest.json` holds: `schemaVersion`, fingerprint, status, cursor, timestamps,
`lastError`, and failed run ids. The **cursor is the single authoritative
progress field**; the completed-run set is reconciled by scanning `runs/`, not
trusted as a second source of truth.

Durability protocol, identical for `run.json` and `manifest.json`:

1. write to a temp file
2. `Flush(flushToDisk: true)`
3. atomic rename over the target
4. commit the manifest the same way

Guarantee: **process-crash safe**; power-loss durability is best-effort and
explicitly out of scope. The checkpoint is advanced only after the run file is
durable, so the checkpoint is never ahead of the files.

Single writer: `manifest.lock` opened `FileShare.None` with PID/liveness. A second
process gets a typed `OutputRootInUse`. Reporting is read-only and tolerates a
concurrent writer by skipping incomplete files.

## Status Machine

```
pending -> in_progress -> completed
                     \--> paused   (run cap or Retry-After abort; resumable)
                     \--> failed   (unrecoverable error; resumable after fix)
```

`completed` means the continuation token is exhausted **and** every page's run
files are durable. Only `completed` short-circuits a re-run. A cap or throttle
abort records `paused`, never `completed`, so a later resume cannot silently
truncate.

## Retrieval Rules

- Fetch build-list pages sequentially; one page in memory at a time.
- Per-run detail only when the list item lacks a required contract field;
  the active `detailPolicy` is part of the fingerprint.
- Retry only `408, 429, 500, 502, 503, 504` plus connection/timeout exceptions.
  **5 attempts (4 retries), waits 1, 2, 4, 8s.** Parse `Retry-After` in both
  delta-seconds and HTTP-date form. A `Retry-After` over 60s persists status
  `paused` **and** exits non-zero with a typed, actionable error telling the
  operator to rerun.
- A detail-fetch throttle aborts the pipeline to `paused`, not just that run.
- `maxRuns` is a runtime budget applied by trimming the page budget,
  `$top = min(pageSize, remaining)`, so overshoot is bounded. It leaves status
  `paused`.
- An invalid/expired continuation token restarts from `cursor=null` and upserts.
- Definition-name wildcard resolution pages and retries like any list call.
- Ctrl+C cancellation propagates through the whole pipeline.

## Security

- No `Pat` member in config; no `--pat` flag. `AZDO_PAT` only.
- Credentials are never logged, persisted, or written to the manifest.
- No absolute local paths in report output; `SourcePath` is removed from the
  report contract.
- `lastError` and quarantined manifests must not capture a PAT or absolute path.
- `.gitignore` excludes `build-analytics.config.json`.

## Resolved Decisions

1. **Retry.** As in Retrieval Rules — 5 attempts, waits 1/2/4/8s; `Retry-After`
   honored up to 60s, then persist `paused` and exit non-zero with a typed error.
2. **Run cap.** `maxRuns` trims the page budget; whole pages are committed; the
   run is left `paused`, never `completed`.
3. **Skewed timestamps.** Any strictly reversed endpoint pair yields `null`,
   never negative. Equal endpoints yield `0.0`.
4. **Summary scope.** Duration columns are expressed in **seconds** (no
   percentiles/cost). Counts and monthly buckets are still produced.
5. **Output.** Excel only in v1, behind `ITimingReportWriter`. An in-memory test
   adapter keeps the seam honest. CSV/JSON and charts are deferred.
6. **CLI.** Breaking rewrite accepted. Legacy flags are removed outright; no
   migration shim and no deprecation/parity phase.
7. **Corrupt manifest.** Quarantine to `manifest.corrupt-<utc>-<guid>.json`,
   rescan `runs/`, restart from `cursor=null`, upsert. Skip only files that are
   `detail`-complete under the active policy.
8. **Schema version.** `schemaVersion: 1`. Any mismatch (older or newer) is a
   typed error directing the operator to a new output root. No migration code,
   and a newer-but-valid manifest is never quarantined as "corrupt".
9. **Rounding.** Domain stores raw seconds. Rounding to 2dp happens only in
   aggregate/report output, using `MidpointRounding.AwayFromZero` (0.125 → 0.13).
   The `>300s` wait threshold compares raw seconds.
10. **Month ordering.** Real UTC months ascending; `(unknown)` sorts last.
11. **Month anchor.** Grouping anchor is `QueueTime` only; no `StartTime`
    fallback. Absent `QueueTime` goes to `(unknown)`.
12. **Monthly metrics.** `RunCount`, `Succeeded`, `Failed`, `PartiallySucceeded`,
    `Canceled`, `NotStarted`, `WaitOverFiveMin`, and the three averages.
13. **Fingerprint identity.** The effective query only (org, project, time range,
    resolved definition ids, detail policy, api version). Raw ids/names are
    informational. Null and empty collections are the same identity.
14. **Run schema is flat.** Fields outside the contract are dropped entirely.
15. **Port shape.** Async, `CancellationToken`-aware, page-oriented `IBuildSource`.
16. **Fingerprint hash.** SHA-256, lowercase hex; a golden-vector test pins it.
17. **Canonical API names.** As recorded in Target Layout, ratified after the
    implementer's design landed. Alternate timing type names are not used.
18. **JSON tolerance.** Unknown fields are ignored on read and never emitted.
19. **Case sensitivity.** `org` and `project` are case-sensitive, preserved as given.
20. **No clock in Core.** `IDelayScheduler` only; adapters own `TimeProvider`.
21. **Incomplete runs count.** `RunCount` and monthly buckets include runs with
    missing timestamps; only averages skip nulls.
22. **Value objects copy inputs.** `TimingSummary.Months` is defensively copied so
    caller mutation cannot change equality or hashing.
23. **JSON settings.** Core exports one canonical `JsonSerializerOptions` as
    `BuildAnalytics.Core.BuildAnalyticsJson.Options`:
    `JsonSerializerDefaults.Web` (camelCase, case-insensitive reads) plus
    `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)`. App adapters and
    tests consume that same instance — no ad-hoc options. Enums serialize as
    strings: `"source":"list"|"detail"`, `"status":"in_progress"`. Literal-JSON
    tests pin this, and a golden exact-shape assertion guards field drift.
24. **Typed storage errors live in Core** (`BuildAnalytics.Core.Errors`):
    `OutputRootInUseException`, `UnsupportedSchemaVersionException`,
    `FingerprintMismatchException`, `CorruptRunFileException`. A valid artifact with
    an unexpected `schemaVersion` (older or newer) throws
    `UnsupportedSchemaVersionException` — not corruption, never quarantined.
25. **Corrupt run files throw.** `IRunStore.TryReadAsync` returns null only when
    the file is absent; an unparseable run file throws `CorruptRunFileException`.
    Only the manifest is quarantined.
26. **Fault seam.** The file adapters depend on `IFileOperations`
    (`WriteTempAsync` / `FlushToDiskAsync` / `RenameAsync`); tests inject a
    decorator that throws or blocks at a chosen stage. Supersedes the proposed
    `IStorageFaults` checkpoint interface — equivalent and cleaner.
27. **`lastError` sanitization** is owned by the error-capture layer (HTTP
    adapter / pipeline), not the store. The store persists what it is given and
    injects no PAT or absolute path.
28. **Fingerprint compatibility** is a pure helper,
    `ManifestCompatibility.EnsureCompatible(manifest, expectedFingerprint, existingRunIds)`,
    called by the pipeline. It throws `FingerprintMismatchException` only when
    `runs/` is non-empty and fingerprints differ. The frozen `IManifestStore` has
    no fingerprint parameter.

29. **`Manifest` is a value object too.** It defensively copies `FailedRunIds` /
    `DefinitionIds` / `DefinitionNames` and implements structural `Equals` /
    `GetHashCode`, matching `TimingSummary`.
30. **Read-only manifest access.** `new FileManifestStore(root, faults?)` is the
    writer and takes the exclusive `manifest.lock` eagerly.
    `FileManifestStore.OpenReadOnly(root)` takes no lock, so reporting can read
    while a writer runs; `CommitAsync` on a read-only instance throws.
31. **Pause signal.** The adapter throws `PipelinePausedException(PauseReason)`
    (`RetryAfterTooLong`, `RunCapReached`, `DetailThrottled`); the pipeline
    persists `paused` and exits non-zero.
32. **Attempts.** 5 total (1 initial + 4 retries), waits 1/2/4/8s. `Retry-After`
    is consulted only on retryable statuses and delta-seconds wins over HTTP-date;
    60s is allowed, >60s pauses, a past date clamps to 0.
33. **Retryable failures.** `HttpRequestException`, `IOException`,
    `SocketException`, and timeout `OperationCanceledException` (caller token not
    cancelled). A cancelled caller token is rethrown, never retried.
34. **Detail-complete rule.** `ListOnly` never needs detail. `FillMissing` needs
    it only when `definitionId`, `definitionName`, `status`, or `result` is null,
    or when a `completed` run is missing any timestamp. `buildNumber`, `reason`,
    `poolId`, `poolName`, `sourceBranch` never trigger detail.
35. **`maxRuns` trimming** is owned by the adapter: `$top = max(1, min(pageSize,
    remaining))`; `0` pauses with no call; negative is rejected.
36. **Invalid continuation token** throws `InvalidContinuationTokenException`
    (also for a repeated token); the adapter does not restart — the pipeline
    restarts from `cursor=null` and upserts.
37. **Definition resolution** is a separate port `IDefinitionResolver`;
    `IBuildSource` stays frozen.
38. **Error text** never includes the raw ADO body: status + sanitized relative
    URL + correlation id only. New error types: `RunNotFoundException`,
    `RetryExhaustedException`, `AdoRequestException`.
39. **Stamping.** List runs get `RunSource.List`, detail runs `RunSource.Detail`;
    `FetchedAt = clock.GetUtcNow()` (the adapter owns `TimeProvider`).
40. **Status comparison** in the detail rule is case-insensitive, matching every
    other status/result comparison in the domain.
41. **Definition resolution** fetches all definitions (paged) and matches
    client-side: case-insensitive anchored wildcard over `name` **or** `path`,
    escaping every regex metacharacter except `*` / `?`; then union, sort, and
    distinct. No `name=` query parameter — the endpoint filter does not cover
    `path`.
42. **Run-file validation.** A deserialized run file must have `Id > 0` **and**
    `Id` equal to its directory key; otherwise `CorruptRunFileException`. A
    wrong-shape or partial file is corrupt, never silently defaulted (a missing
    `id` must not create a bogus `runs/0` entry). A requested `runId <= 0` throws
    `ArgumentOutOfRangeException`; `WriteAsync` likewise rejects a run with
    `Id <= 0`, and `ListRunIdsAsync` skips non-positive directories.
43. **Flush is verified, not assumed.** Tests assert `FlushToDiskAsync` runs on a
    non-empty temp before rename and fault-inject at write-temp and flush; true
    fsync durability is explicitly out of unit-test scope.
44. **Lock failure typing and cleanup.** Only "already locked" maps to
    `OutputRootInUseException`; other IO failures map to `StorageException`. The
    opened lock stream is disposed on any post-open failure.
45. **Delete-sharing reads.** Run and manifest reads open with
    `FileShare.ReadWrite | FileShare.Delete`, so a concurrent reader cannot block
    the writer's rename on Windows. The flags come from a small testable seam so a
    regression back to `FileShare.Read` fails a test; real Windows behaviour is
    manual verification.
46. **Quarantine** honors cancellation; a quarantine IO failure surfaces as
    `StorageException`, never a raw `IOException`.
47. **Manifest validation.** A missing/empty `Fingerprint` or missing `status` is
    malformed and is quarantined like other corrupt content.
48. **`StorageException`** (unexpected storage IO failure) joins `Core.Errors`.
49. **Lock-failure classification** is a pure helper: a sharing-violation HResult
    (`0x80070020`) or Unix `EWOULDBLOCK`/`EAGAIN` means contention
    (`OutputRootInUseException`); anything else means `StorageException`.
50. **Required manifest fields** — `schemaVersion` (present and current),
    `fingerprint` (non-empty), `status`, `createdAt`, `updatedAt`. Missing any is
    corruption and quarantines; `schemaVersion` is validated first so a
    valid-but-wrong version never quarantines.
51. **`Retry-After` replaces** the scheduled backoff for that attempt; a value
    below the schedule still wins, and a negative/past value clamps to 0
    (retry immediately). With no `Retry-After`, the 1/2/4/8 schedule applies.
52. **60s boundary.** Exactly 60.000s is allowed; only strictly >60s pauses.
53. **Detail throttle.** A 429 on a detail fetch retries per policy; on
    exhaustion it throws `PipelinePausedException(DetailThrottled)` so the whole
    pipeline pauses rather than skipping the run. A >60s `Retry-After` pauses
    immediately with `RetryAfterTooLong`.
54. **Timeout vs cancellation.** Classify using the caller token's
    `IsCancellationRequested`: if cancelled, rethrow and never retry; otherwise a
    `TaskCanceledException`/`OperationCanceledException` is a per-attempt timeout
    and is retryable. Unwrap `HttpRequestException.InnerException` for
    `SocketException`/`IOException`.
55. **`ListAsync` issues exactly one GET per call.** The pipeline owns paging,
    repeated-token detection, and restart-on-invalid; the adapter only throws
    `InvalidContinuationTokenException` on a server 400.
56. **Token plumbing.** Read `x-ms-continuationtoken` from the response header;
    send it back as the `continuationToken` query parameter.
57. **Mapping table.** `definition.id`→`definitionId`, `definition.name`→
    `definitionName`, `queue.pool.id`→`poolId`, `queue.pool.name`→`poolName`, plus
    top-level `id`, `buildNumber`, `queueTime`, `startTime`, `finishTime`,
    `status`, `result`, `reason`, `sourceBranch`. Nothing else is mapped.
58. **`DetailPolicyEvaluator`** is a pure Core helper tested in slice 3. A
    `completed` run requires all three timestamps; a null `definitionId` or a
    blank `definitionName`/`status`/`result` counts as missing.
59. **`maxRuns`.** Default page size 1000; `$top = max(1, min(pageSize, remaining))`
    on every page; when remaining reaches 0 the next call throws
    `PipelinePausedException(RunCapReached)` with no HTTP call; `0` pauses on the
    first call; negative throws `ArgumentOutOfRangeException`.
60. **`IDefinitionResolver.ResolveAsync(BuildQuery query, IReadOnlyList<string> patterns, CancellationToken cancellationToken)`**
    returns definition ids only (union, sorted, distinct); the caller passes the
    patterns explicitly (typically from `BuildQuery.DefinitionNames`). `DetailPolicyEvaluator.NeedsDetail(BuildRun run, DetailPolicy policy)`
    is the pure Core predicate. `PipelinePausedException` exposes `PauseReason Reason`,
    `TimeSpan? RetryAfter`, `int? RemainingBudget`.
61. **Error sanitization.** Message = status + relative path (no host, no query)
    + request id from `x-ms-request-id`, else `x-vss-activity-id`, else
    `x-ms-correlation-request-id`. Never the body or the PAT.
62. **`PipelinePausedException`** carries `Reason` plus optional `RetryAfter` /
    `RemainingBudget`. The pipeline catches it, persists `Manifest.Status = Paused`,
    and **returns** rather than rethrowing. The typed values are surfaced on
    `RetrievalResult` as `PauseReason? Pause`, `TimeSpan? RetryAfter`, and
    `int? RemainingBudget` (never parsed back out of `lastError` text), and the
    CLI maps a non-`completed` status to a non-zero exit.
63. **Stamping** always uses the injected clock; `FetchedAt` is never taken from
    the response.
64. **Slice-3 tests** inject `HttpMessageHandler` + `TimeProvider` +
    `IDelayScheduler`; no sockets, no `Task.Delay`.
65. **Read-only stores never mutate.** On a read-only `FileManifestStore`,
    `TryReadAsync` returns null for absent or corrupt content and performs **no**
    quarantine; a valid-but-wrong `schemaVersion` still throws
    `UnsupportedSchemaVersionException`; an IO failure throws `StorageException`.
    Quarantine is gated on writer mode, so a reader can never steal the manifest
    from a concurrent writer.
66. **Pipeline location.** `BuildAnalytics.App.Retrieval`: it stamps
    `CreatedAt`/`UpdatedAt`, and ADR-20 forbids a clock in Core. Core keeps the
    pure helpers (fingerprint, `ManifestCompatibility`, `DetailPolicyEvaluator`).
67. **Partial-page failures.** A detail 404 skips the run, records its id in
    `FailedRunIds`, and advances the cursor. A storage failure on `WriteAsync`
    aborts with `Failed` and does **not** advance — a checkpoint never advances
    past a run that was not durably written. Token exhausted with a non-empty
    `FailedRunIds` is still `completed`.
68. **Initial commit.** An `in_progress` manifest (fingerprint, `cursor=null`,
    timestamps) is written before the first list call, so a crash leaves a
    fingerprinted, resumable root.
69. **Invalid/repeated token.** Restart from `cursor=null` **once** per run; a
    second invalid or repeated token fails the run. Never loop.
70. **Cancellation** leaves the last committed status (`in_progress`), does not
    advance the cursor, and performs no cleanup commit with the cancelled token.
71. **Short-circuit.** Read the manifest first; a `completed` manifest returns
    before resolving ids or calling the source.
72. **Token-error classification.** A 400 is `InvalidContinuationTokenException`
    **only** when a continuation token was sent on that request; a 400 with no
    token is a permanent `AdoRequestException`. This bounds pipeline restarts to
    genuine token failures.
73. **Only a detail 404 is a per-run skip.** A detail `RunNotFoundException`
    records `FailedRunIds` and advances. Every other detail failure —
    `AdoRequestException` (401/403/409/422), an unexpected exception, or any
    non-404 HTTP error — aborts with `Status = Failed` and does not advance the
    cursor, because such failures usually affect every run.
74. **Reporting read path.** Reuses `IRunStore` plus a read-only `IManifestStore`
    (`OpenReadOnly`); no new port. Reporting performs no writes and has no network
    dependency.
75. **No completed retrieval.** An absent manifest or `Status != completed`
    throws a typed `ReportingErrorException` (CLI exits non-zero). A completed
    root with zero runs yields a valid zero-filled report.
76. **Excel contract.** `Overview` (Metric/Value: Runs, Succeeded, Failed,
    Partially Succeeded, Canceled, Not Started, Wait > 5 Min, and the three
    averages) and `Monthly` (Month + the same columns) — real UTC months
    ascending, `(unknown)` last. The Runs sheet is dropped. Null averages render
    blank; the destination path is adapter-owned (default
    `<outputRoot>/timing-report.xlsx`).
77. **Reporting failures.** A corrupt run file is skipped and counted; an
    unsupported run `schemaVersion` aborts; a listed id whose file is absent is
    skipped silently.
78. **Rebuild skip.** For each listed run whose id already exists on disk, read
    the existing file; if `DetailPolicyEvaluator.NeedsDetail(existing, policy)` is
    false, skip both the detail fetch **and** the write. This avoids re-fetching on
    a rebuild and, more importantly, prevents downgrading a detail-complete file
    to a thin list row. A corrupt on-disk file is treated as missing.
83. **Stale-schema run files are repaired, not fatal.** In the rebuild skip, an
    on-disk run with an unsupported `schemaVersion` is treated as missing and
    re-fetched/rewritten — run files are a cache and retrieval can self-heal. The
    **manifest** schema mismatch stays fatal (ADR-8), and **reporting** still
    aborts on an unsupported run schema (ADR-77) because it has no network and
    cannot repair; skipping would yield a confidently incomplete report.
84. **Unexpected exceptions fail the run.** After the OCE rethrow, the pipeline
    catches **all** exceptions, persists `Manifest.Status = Failed` with a
    sanitized `lastError`, and returns `Failed` without advancing the cursor.
    The adapter also wraps a malformed body (`JsonException`) in a typed error so
    it is classified rather than escaping.
85. **Token-cycle detection.** All seen continuation tokens are tracked; any
    revisit (including a period > 1 cycle) is treated as repeated/invalid —
    restart once from `cursor=null`, then fail. The same guard applies to
    definition-resolution paging.
86. **Detail identity.** A detail response whose id differs from the requested
    `runId` (or is non-positive) is a protocol anomaly, not a 404: it aborts with
    `Failed` and is never treated as a per-run skip.
87. **Empty continuation header** means “no token”; a null or whitespace
    `x-ms-continuationtoken` must not be echoed as a real token.
88. **Fingerprint before short-circuit** (amends ADR-71). Order: read manifest →
    resolve ids → fingerprint → `ManifestCompatibility.EnsureCompatible` → then,
    if `completed`, return `ShortCircuited`. A completed root still issues no
    list calls, but a *different* query on a non-empty root is a typed error
    rather than a silent no-op.
89. **Pass-written runs join the skip set** (amends the earlier exclusion, which
    was a regression). After a successful `WriteAsync`, the written run is added
    to the in-memory skip set so a same-pass re-list — e.g. a token restart
    re-listing from `cursor=null` — skips it with no re-read, no re-fetch, and no
    detail→list downgrade. Without this, a restart triggers a full duplicate
    pass over every already-written run.
90. **`InvalidDetailPayloadException(requestedRunId, returnedRunId)`** joins
    `Core.Errors` for a detail body whose id does not match the request.
91. **Unexpected-failure rule.** Any non-cancellation exception in `RunAsync`
    triggers a **best-effort** `CommitAsync(Status = Failed, sanitized lastError)`;
    if that succeeds, return `Failed` without advancing. If the Failed commit
    itself throws, propagate. `OperationCanceledException` is rethrown with no
    commit. (This replaces the earlier “commit failure propagates” rule.)
92. **Completed roots are bound to their query.** For a `completed` manifest a
    fingerprint mismatch is a typed error **regardless** of `runs/`, even when the
    root is empty. The empty-runs allowance applies only when no manifest exists
    or the manifest is not completed.
93. **Seen-token set resets on restart.** The set must **not** persist across the
    single restart, because the first post-restart token is normally one already
    seen — persisting it would fail the restart immediately and remove the
    recovery path. Termination comes from the one-restart bound.
94. **CLI top-level error handling.** After mapping `OperationCanceledException`
    to `130`, `Program` catches every other exception, writes a sanitized message
    to stderr (no stack trace, no PAT, no absolute path), and exits `1`. This
    covers resolver/manifest failures raised before or around the pipeline, which
    the pipeline's own catch-all cannot.
95. **`ReportingWriteException`.** The report write path wraps IO failures in a
    typed `Core.Errors.ReportingWriteException` whose message contains only the
    file **name** and the reason — never the absolute destination path. The
    ADR-94 CLI catch remains a second sanitization layer.
96. **Progress reporting.** `RetrievalPipeline` takes an optional
    `IRetrievalProgress` (no-op by default) and reports a start line, one line per
    page, a percentage tick at each 5% boundary, invalid-token restarts, pauses,
    and completion. The percentage is `(on-disk baseline + handled this pass) /
    TotalCount`, where `TotalCount` comes from the ADO `count` field (`BuildPage`
    gains `int? TotalCount`); without a count, page/run lines only. Progress goes
    to stderr and `--quiet` suppresses it; there are no per-detail lines.
97. **Incremental refresh is the default.** A `completed` manifest no longer
    short-circuits. A re-run re-lists from `cursor=null`, skips runs already on
    disk (ADR-78), writes the new ones, and **stops early once a page adds no new
    runs** (the list is `queueTimeDescending`, so older pages are already stored).
    `FailedRunIds` are retried. Status returns to `in_progress` and then
    `completed`; `CreatedAt` is preserved and `UpdatedAt` refreshed. The
    fingerprint check (ADR-88/92) still runs first.
98. **Config file.** Optional JSON at `build-analytics.config.json` (working
    directory) or `--config <path>`. Precedence: CLI → config → built-in default;
    there is no environment tier for these values. Keys: `org`, `project`,
    `outputRoot`, `apiVersion`, `detail`, `maxRuns`, `quiet`, `out`. A `pat` key is
    a **hard usage error** naming `AZDO_PAT`. A loader performs the IO so
    `CliParser` stays pure.
99. **Removed unused surface.** `--from`, `--to`, `--page-size`,
    `--definition-id`, and `--definition` are gone, as are their config keys; page
    size is a fixed internal constant (1000). `BuildQuery` drops `MinTime`,
    `MaxTime`, and the definition fields, so the fingerprint is now
    `org`/`project`/`detailPolicy`/`apiVersion` and its golden vector is
    re-pinned. `IDefinitionResolver`, the definitions endpoint call, and
    `AdoWildcard` are deleted. This supersedes the CLI-surface parts of ADR-79 and
    ADR-37/41/60.
100. **Refresh retries outstanding failures.** A refresh keeps the manifest's
    `FailedRunIds` as a `pendingFailed` set; a re-attempted id leaves the set, and
    the early stop additionally requires the set to be empty (a failed run is not
    "already stored", so the early-stop rationale does not apply while failures
    are outstanding). Ids never re-listed are preserved, never dropped. Cost: list
    pagination runs further while failures exist — list calls only, no detail
    overfetch.
101. **Config values are validated like CLI values.** A negative `maxRuns` in the
    config file is a usage error (exit 2), matching `--max-runs -1`.
102. **Progress counts only new ids.** `handled` counts ids not already in the
    on-disk baseline, so repairing an incomplete file does not inflate the
    percentage; the reported percent is clamped to 100.
103. **`Manifest` drops `DefinitionIds`/`DefinitionNames`**, dead after ADR-99.
79. **CLI surface.** Verbs `retrieve` / `report` / `help`. `retrieve` takes
    `--org`, `--project`, `--output-root` (required) plus `--from`, `--to`,
    `--definition-id` (repeatable), `--definition` (repeatable glob), `--detail`,
    `--max-runs`, `--page-size`, `--api-version`, `--quiet`; `report` takes
    `--output-root` (required), `--out`, `--quiet`. **No config file and no
    `--config`** — flags plus `AZDO_PAT` only. The PAT is read through an
    injectable credential seam, never a flag, never persisted.
80. **Exit codes.** `0` success/help, `2` usage/parse error, `1` runtime or typed
    failure (including `paused` and `failed`), `130` on Ctrl+C. `Parse` returns a
    result; only `Main` maps it to an exit code — no `Environment.Exit` in parsing.
81. **CLI defaults.** `--detail` defaults to `ListOnly`; API version `7.1` (an
    identity input); page size 1000; report `--out` defaults to
    `<outputRoot>/timing-report.xlsx`. ADO auth is HTTP Basic with an empty
    username and the PAT as password.
82. **Output channels.** Reports and usage go to stdout; progress and errors go
    to stderr; `--quiet` suppresses non-error progress. No `--verbose` in v1.

Accepted limitations (documented, no action): stale `.tmp` files are ignored by
`ListRunIdsAsync` and are not garbage-collected at startup; `Manifest` list
equality is order-sensitive, which is safe while construction stays canonical;
when a non-conforming response carries **both** `Retry-After` forms the executor
takes header order rather than strictly preferring delta-seconds; `FileRunStore.TryReadAsync`
does not wrap a raw read `IOException` in `StorageException` (upstream layers
handle it); detail `424` is covered by the generic non-404 abort path with no
separately named row.

## Design Review Disposition (F1–F17)

All findings accepted and folded into the sections above.

| ID | Finding | Resolution |
|---|---|---|
| F1 | Concurrent writers on one output root | Lock file + typed `OutputRootInUse`; reader tolerates writers |
| F2 | Manifest durability unspecified | Single temp→flush→rename protocol for both files; scope stated |
| F3 | `maxRuns` overshoot (~1000x) | Trim `$top` to the remaining budget; leave `paused` |
| F4 | `<id>__<name>` breaks upsert | Canonical `runs/<runId>/run.json` |
| F5 | Two sources of truth | Cursor authoritative; reconcile by scan; `source` in each file |
| F6 | Core charter ambiguous | Core = pure + ports; all IO in App; architecture test |
| F7 | Detail opt-in undefined | Enumerated `run.json` contract; policy in fingerprint |
| F8 | Fingerprint incomplete/mis-scoped | Fingerprint raw inputs; drop `outputRoot`/`maxRuns` |
| F9 | Status machine undefined | Explicit machine; cap/throttle → `paused` |
| F10 | No seam for crash tests | Inject store/FS port; tests fail between write/rename/commit |
| F11 | Slices under-define contracts | Slice 1 ships all ports + query/fingerprint + models |
| F12 | PAT/paths contradict security | Env-only PAT; drop `--pat` and `SourcePath` |
| F13 | Schema upgrade path | Typed error on mismatch; no migration |
| F14 | Retry math ambiguous | 5 attempts, waits 1/2/4/8s; both `Retry-After` forms |
| F15 | Q4 wording vs monthly sheet | Reworded: duration columns are seconds; counts retained |
| F16 | Q6 vs "deprecate after parity" | Legacy flags deleted outright |
| F17 | Smaller gaps | `runs.json` dropped; token restart; GUID quarantine; list retry |

## Test Matrix (high value)

**A — resumable retrieval.** Sequential multi-page token forwarding; resume from
manifest cursor; crash-before-commit replay without duplicates; repeated token
bounded; empty first page idempotent; **no per-run overfetch** (0 detail calls
when the list item satisfies the contract); Ctrl+C cancellation; one page in memory.

**B — manifest + raw files.** Atomic `run.json` write; checkpoint advanced only
after durable file write; checkpoint never ahead of files; by run id; durability across reopen; fingerprint scoping; status machine;
single-writer lock; no credentials or absolute paths in artifacts.

**C — retry/backoff.** Deterministic waits 1/2/4/8s; both `Retry-After` forms;
abort to `paused` over 60s; transient retried / permanent fails fast; exhaustion
throws typed error; cancellation mid-backoff stops; retried detail fetch idempotent.

**D — timing (pure).** Durations as raw seconds; missing/skewed → null; equal →
0; averages ignore nulls, round only at output, and are null when all null; UTC
month grouping with `(unknown)` sorted last; wait > 5 min strictly > 300 on raw
seconds; case-insensitive counts; purity guard.

**E — reporting/security.** Reporting never touches the network; no absolute
paths in output; PAT never logged or written; `.gitignore` excludes the config.

Constraints: no real network; no `Task.Delay` in tests; no ambient clock inside
pure logic; each test gets its own output root.

## TDD Slices

1. **Contracts + pure timing.** All ports, `BuildQuery` + fingerprint, models,
   `IDelayScheduler` seam, and the canonical types (`Models.BuildRun`,
   `TimingCalculator`, `RunTiming`, `MonthlyTimingRollup`, `TimingTotals`,
   `TimingSummary`, `MonthlyTimingSummary`). No adapters. (F6/F11; ratified ADR-17.)
2. **Manifest + run store adapters.** Atomicity, lock, restart, idempotent
   upsert, crash-before-commit fault injection.
3. **ADO source adapter.** Paging, continuation forwarding, retry, no overfetch.
4. **Pipeline.** list → select missing → (detail if needed) → write → checkpoint.
5. **Reporting.** Summary from local raw files; Excel adapter + in-memory adapter.
6. **CLI.** New `retrieve`/`report` verbs; legacy flags deleted.

Each slice: failing test → minimal implementation → refactor → full suite green.

## Handoff

| Role | Deliverable |
|---|---|
| implementer | Execute slices 1–3 test-first on `rewrite/impl` |
| tester | Own D1–D9; verify each slice independently |
| reviewer | Critique implementation against SOLID/over-engineering before merge |

## Environment

- .NET SDK 10.0.401 installed.
- Baseline verified: `dotnet test build-analytics.slnx` → 15/15 pass on `da40a14`.
- Existing code builds with 3 nullable warnings; reference-only.

## Progress

- **Slice 1 complete** at `ad6f1dc` on `rewrite/impl`: contracts, ports, pure
timing, fingerprint, purity guard. 91/91 tests, 0 warnings. Legacy sources
removed; root is solution + docs + `src/` + `tests/`.
- **FINAL SIGN-OFF** at `8de40c6`: 381/381 tests, 0 warnings, clean tree; all six
slices verified against the committed SHA. The app is complete: resumable,
crash-safe, throttle-aware Azure DevOps retrieval with local Excel reporting
and a clean CLI.
