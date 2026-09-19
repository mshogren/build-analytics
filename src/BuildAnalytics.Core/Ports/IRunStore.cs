using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Result of reading the append-only run log (ADR-109). <see cref="Runs"/> is deduped
/// (last line wins) and sorted by id. Malformed/unsupported lines are excluded from
/// <see cref="Runs"/> and counted so the caller can repair or abort.
/// </summary>
public sealed record RunReadResult(
    IReadOnlyList<BuildRun> Runs,
    int MalformedLineCount,
    int UnsupportedSchemaLineCount);

/// <summary>
/// Stores raw run payloads in a single append-only log (<c>runs.jsonl</c>), one compact
/// JSON object per line. There is no per-id canonical path; the id inside a line is
/// authoritative.
/// </summary>
public interface IRunStore
{
    Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken);

    Task AppendAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken);

    Task ReplaceAllAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken);
}
