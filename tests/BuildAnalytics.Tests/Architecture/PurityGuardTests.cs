using System.Runtime.CompilerServices;

namespace BuildAnalytics.Tests.Architecture;

/// <summary>
/// Enforces the Core charter: the pure project must not reference IO, the network,
/// an ambient clock, or process-wide environment state.
/// </summary>
public sealed class PurityGuardTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "System.IO",
        "System.Net",
        "HttpClient",
        "ClosedXML",
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTime.Today",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "Random",
        "Guid.NewGuid",
        "Environment."
    ];

    [Fact]
    public void Core_sources_contain_no_io_network_clock_or_ambient_environment_usage()
    {
        var coreRoot = Path.Combine(RepoRoot(), "src", "BuildAnalytics.Core");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var token in ForbiddenTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} contains '{token}'");
                }
            }
        }

        Assert.Empty(violations);
    }

    private static string RepoRoot([CallerFilePath] string callerPath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
