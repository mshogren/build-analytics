using System.Text;
using System.Text.Json;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// File-backed run store. Canonical layout (ADR-109): one append-only
/// <c>&lt;outputRoot&gt;/runs.jsonl</c> with one compact JSON object per line. Reads dedupe by
/// id (last line wins); compaction rewrites atomically via <see cref="AtomicFileWriter"/>.
/// </summary>
public sealed class FileRunStore : IRunStore
{
    private readonly string _logPath;
    private readonly IFileOperations _fileOperations;
    private readonly AtomicFileWriter _writer;

    public FileRunStore(string outputRoot, IFileOperations fileOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        _logPath = Path.Combine(Path.GetFullPath(outputRoot), "runs.jsonl");
        _fileOperations = fileOperations;
        _writer = new AtomicFileWriter(fileOperations);
    }

    public async Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
        {
            return new RunReadResult([], 0, 0);
        }

        var bytes = await StorageFileAccess.ReadAllBytesAsync(_logPath, cancellationToken).ConfigureAwait(false);
        var runs = new Dictionary<int, BuildRun>();
        var lastValidLineById = new Dictionary<int, int>();
        var unsupportedCandidates = new List<(int LineIndex, int? RunId)>();
        var malformed = 0;
        var lineIndex = 0;

        foreach (var line in SplitLines(bytes))
        {
            var index = lineIndex++;
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                    schemaVersionElement.ValueKind != JsonValueKind.Number ||
                    !schemaVersionElement.TryGetInt32(out var schemaVersion))
                {
                    malformed++;
                    continue;
                }

                if (schemaVersion != BuildRun.CurrentSchemaVersion)
                {
                    // F1: the id is decodable (valid JSON, wrong version), so a later valid
                    // line for the same run can supersede this one and heal the count.
                    unsupportedCandidates.Add((index, TryReadPositiveId(root)));
                    continue;
                }

                var run = root.Deserialize<BuildRun>(BuildAnalyticsJson.Options);
                if (run is null || run.Id <= 0)
                {
                    malformed++;
                    continue;
                }

                // ADR-109: a later line with the same id supersedes an earlier one.
                runs[run.Id] = run;
                lastValidLineById[run.Id] = index;
            }
            catch (JsonException)
            {
                // A truncated/unparseable line (including a crash tail) is skipped and counted.
                malformed++;
            }
        }

        // F1: an unsupported line is counted only while no later valid line for the same run
        // exists. That is what makes a re-fetched run self-healing and lets compaction run again.
        var unsupported = unsupportedCandidates.Count(candidate =>
            candidate.RunId is not { } id ||
            !lastValidLineById.TryGetValue(id, out var validIndex) ||
            validIndex < candidate.LineIndex);

        var ordered = runs.Values.OrderBy(run => run.Id).ToArray();
        return new RunReadResult(ordered, malformed, unsupported);
    }

    public Task AppendAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);

        if (runs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var content = SerializeLines(runs);
        if (EndsWithoutNewline())
        {
            // N1: a crash can leave an unterminated fragment; never glue the new record onto it.
            var padded = new byte[content.Length + 1];
            padded[0] = (byte)'\n';
            content.Span.CopyTo(padded.AsSpan(1));
            content = padded;
        }

        return _fileOperations.AppendAsync(_logPath, content, cancellationToken);
    }

    public Task ReplaceAllAsync(IReadOnlyList<BuildRun> runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var content = runs.Count == 0 ? ReadOnlyMemory<byte>.Empty : SerializeLines(runs);
        return _writer.WriteAsync(_logPath, content, cancellationToken);
    }

    private static ReadOnlyMemory<byte> SerializeLines(IReadOnlyList<BuildRun> runs)
    {
        using var stream = new MemoryStream();
        foreach (var run in runs)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (run.Id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(runs), run.Id, "Run id must be positive.");
            }

            // BuildAnalyticsJson is compact (no indentation); one object per newline-terminated line.
            JsonSerializer.Serialize(stream, run, BuildAnalyticsJson.Options);
            stream.WriteByte((byte)'\n');
        }

        return stream.ToArray();
    }

    private static IEnumerable<string> SplitLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split('\n'))
        {
            yield return line.EndsWith('\r') ? line[..^1] : line;
        }
    }

    private static int? TryReadPositiveId(JsonElement root)
        => root.TryGetProperty("id", out var idElement)
           && idElement.ValueKind == JsonValueKind.Number
           && idElement.TryGetInt32(out var id)
           && id > 0
            ? id
            : null;

    private bool EndsWithoutNewline()
    {
        if (!File.Exists(_logPath))
        {
            return false;
        }

        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, StorageFileAccess.ReadShare);
        if (stream.Length == 0)
        {
            return false;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }
}
