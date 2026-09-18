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
/// Corrupt/manifest-schema mismatches are typed; malformed manifests are quarantined and reported absent.
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

        var bytes = await StorageFileAccess.ReadAllBytesAsync(_manifestPath, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            if (!TryReadSchemaVersion(root, out var schemaVersion))
            {
                Quarantine(cancellationToken);
                return null;
            }

            if (schemaVersion != Manifest.CurrentSchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(Manifest.CurrentSchemaVersion, schemaVersion);
            }

            if (!HasRequiredFields(root))
            {
                Quarantine(cancellationToken);
                return null;
            }

            var manifest = root.Deserialize<Manifest>(BuildAnalyticsJson.Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Fingerprint))
            {
                Quarantine(cancellationToken);
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            Quarantine(cancellationToken);
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

    private static bool TryReadSchemaVersion(JsonElement root, out int schemaVersion)
    {
        schemaVersion = 0;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("schemaVersion", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out schemaVersion);
    }

    private static bool HasRequiredFields(JsonElement root)
        => IsNonEmptyString(root, "fingerprint")
           && root.TryGetProperty("status", out _)
           && root.TryGetProperty("createdAt", out _)
           && root.TryGetProperty("updatedAt", out _);

    private static bool IsNonEmptyString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element)
           && element.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(element.GetString());

    private FileStream AcquireLock()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (LockFailureClassifier.Classify(exception) == LockFailure.Contention)
        {
            throw new OutputRootInUseException(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageException("Could not open the manifest lock.", exception);
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}\n{_timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}");
            stream.SetLength(0);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return stream;
        }
        catch (Exception exception)
        {
            stream.Dispose();
            throw new StorageException("Could not initialize the manifest lock.", exception);
        }
    }

    private void Quarantine(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(_outputRoot, $"manifest.corrupt-{timestamp}-{Guid.NewGuid():N}.json");

        try
        {
            File.Move(_manifestPath, target, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageException("Could not quarantine the corrupt manifest.", exception);
        }
    }
}
