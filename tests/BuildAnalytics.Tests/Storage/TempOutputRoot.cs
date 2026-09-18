using System.Globalization;

namespace BuildAnalytics.Tests.Storage;

/// <summary>Per-test isolated output root under the system temp directory.</summary>
internal sealed class TempOutputRoot : IDisposable
{
    public TempOutputRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "build-analytics-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
