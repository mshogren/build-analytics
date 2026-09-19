using System.Text;
using System.Text.Json;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// File-backed run store. Canonical layout (ADR-109/110): one append-only
/// <c>&lt;outputRoot&gt;/runs.jsonl</c> with one compact JSON object per line. Reads dedupe by
/// id (last line wins) and skip malformed lines.
/// </summary>
public sealed class FileRunStore : IRunStore
{
    public const string LogFileName = "runs.jsonl";

    private readonly string _logPath;
    private readonly IFileOperations _fileOperations;

    public FileRunStore(string outputRoot, IFileOperations fileOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        _logPath = Path.Combine(Path.GetFullPath(outputRoot), LogFileName);
        _fileOperations = fileOperations;
    }

    public async Task<RunReadResult> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
        {
            return new RunReadResult([], 0);
        }

        var bytes = await StorageFileAccess.ReadAllBytesAsync(_logPath, cancellationToken).ConfigureAwait(false);
        var runs = new Dictionary<int, BuildRun>();
        var malformed = 0;

        foreach (var line in SplitLines(bytes))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                // ADR-110: the log is only ever written by this build, so an unexpected or
                // missing schemaVersion is treated like any other malformed line.
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                    schemaVersionElement.ValueKind != JsonValueKind.Number ||
                    !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
                    schemaVersion != BuildRun.CurrentSchemaVersion)
                {
                    malformed++;
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
            }
            catch (JsonException)
            {
                // A truncated/unparseable line (including a crash tail) is skipped and counted.
                malformed++;
            }
        }

        var ordered = runs.Values.OrderBy(run => run.Id).ToArray();
        return new RunReadResult(ordered, malformed);
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
            // A crash can leave an unterminated fragment; never glue the new record onto it.
            var padded = new byte[content.Length + 1];
            padded[0] = (byte)'\n';
            content.Span.CopyTo(padded.AsSpan(1));
            content = padded;
        }

        return _fileOperations.AppendAsync(_logPath, content, cancellationToken);
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
