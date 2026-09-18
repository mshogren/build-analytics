using System.Globalization;
using System.Text.Json;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// File-backed run store. Canonical layout: <c>&lt;outputRoot&gt;/runs/&lt;runId&gt;/run.json</c>.
/// Writes are atomic and upsert by run id.
/// </summary>
public sealed class FileRunStore : IRunStore
{
    private readonly string _runsRoot;
    private readonly AtomicFileWriter _writer;

    public FileRunStore(string outputRoot, IFileOperations fileOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        _runsRoot = Path.Combine(Path.GetFullPath(outputRoot), "runs");
        _writer = new AtomicFileWriter(fileOperations);
    }

    public Task WriteAsync(BuildRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var content = JsonSerializer.SerializeToUtf8Bytes(run, BuildAnalyticsJson.Options);
        return _writer.WriteAsync(RunPath(run.Id), content, cancellationToken);
    }

    public async Task<BuildRun?> TryReadAsync(int runId, CancellationToken cancellationToken)
    {
        var path = RunPath(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                !schemaVersionElement.TryGetInt32(out var schemaVersion))
            {
                throw new CorruptRunFileException(runId);
            }

            if (schemaVersion != BuildRun.CurrentSchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(BuildRun.CurrentSchemaVersion, schemaVersion);
            }

            return root.Deserialize<BuildRun>(BuildAnalyticsJson.Options)
                ?? throw new CorruptRunFileException(runId);
        }
        catch (JsonException exception)
        {
            throw new CorruptRunFileException(runId, exception);
        }
    }

    public Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_runsRoot))
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        var ids = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories(_runsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, "run.json")))
            {
                ids.Add(id);
            }
        }

        ids.Sort();
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    private string RunPath(int runId)
        => Path.Combine(_runsRoot, runId.ToString(CultureInfo.InvariantCulture), "run.json");
}
