using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Result of reading the append-only run log (ADR-109/110). <see cref="Runs"/> is deduped
/// (last line wins) and sorted by id. A malformed line - including one with an unexpected
/// <c>schemaVersion</c> - is skipped and counted.
/// </summary>
public sealed record RunReadResult(IReadOnlyList<BuildRun> Runs, int MalformedLineCount);

/// <summary>
/// Stores raw run payloads in a single append-only log (<c>runs.jsonl</c>), one compact
/// JSON object per line. There is no per-id canonical path; the id inside a line is
/// authoritative.
/// </summary>
public interface IRunStore
{
    Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken);

    Task AppendAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken);
}
