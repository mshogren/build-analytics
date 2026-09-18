using System.Globalization;
using System.Text;
using System.Text.Json;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// File-backed manifest store with a single-writer lock and atomic commits.
/// Corrupt manifests are quarantined and reported as absent; schema mismatches are typed errors.
/// Reporting uses <see cref="OpenReadOnly(string, TimeProvider?)"/> so it can read while a writer is alive.
/// </summary>
public sealed class FileManifestStore : IManifestStore, IDisposable
{
    private readonly string _outputRoot;
    private readonly string _manifestPath;
    private readonly string _lockPath;
    private readonly AtomicFileWriter? _writer;
    private readonly TimeProvider _timeProvider;
    private FileStream? _lock;

    public FileManifestStore(string outputRoot, IFileOperations fileOperations, TimeProvider? timeProvider = null)
        : this(outputRoot, fileOperations, timeProvider, readOnly: false)
    {
    }

    private FileManifestStore(string outputRoot, IFileOperations? fileOperations, TimeProvider? timeProvider, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        _outputRoot = Path.GetFullPath(outputRoot);
        _manifestPath = Path.Combine(_outputRoot, "manifest.json");
        _lockPath = Path.Combine(_outputRoot, "manifest.lock");
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (readOnly)
        {
            _writer = null;
            return;
        }

        ArgumentNullException.ThrowIfNull(fileOperations);
        Directory.CreateDirectory(_outputRoot);
        _writer = new AtomicFileWriter(fileOperations);
        _lock = AcquireLock();
    }

    /// <summary>Opens a read-only view that takes no writer lock and can read during an active writer.</summary>
    public static FileManifestStore OpenReadOnly(string outputRoot, TimeProvider? timeProvider = null)
        => new(outputRoot, fileOperations: null, timeProvider, readOnly: true);

    public async Task<Manifest?> TryReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(_manifestPath, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                !schemaVersionElement.TryGetInt32(out var schemaVersion))
            {
                Quarantine();
                return null;
            }

            if (schemaVersion != Manifest.CurrentSchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(Manifest.CurrentSchemaVersion, schemaVersion);
            }

            var manifest = root.Deserialize<Manifest>(BuildAnalyticsJson.Options);
            if (manifest is null)
            {
                Quarantine();
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            Quarantine();
            return null;
        }
    }

    public Task CommitAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (_writer is null)
        {
            throw new InvalidOperationException("This manifest store is read-only and cannot commit.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, BuildAnalyticsJson.Options);
        return _writer.WriteAsync(_manifestPath, content, cancellationToken);
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private FileStream AcquireLock()
    {
        try
        {
            var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var payload = Encoding.UTF8.GetBytes(
                $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}\n{_timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}");
            stream.SetLength(0);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return stream;
        }
        catch (IOException exception)
        {
            throw new OutputRootInUseException(exception);
        }
    }

    private void Quarantine()
    {
        var timestamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(_outputRoot, $"manifest.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
        File.Move(_manifestPath, target, overwrite: false);
    }
}
