using System.Runtime.CompilerServices;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Architecture;

/// <summary>
/// Enforces the Core charter. Primary checks are structural (assembly references and the
/// project file); the lexical scan is a secondary belt-and-braces check.
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
        "File.",
        "Directory.",
        "FileStream",
        "Task.Delay",
        "Thread.Sleep",
        "Stopwatch",
        "Random",
        "Guid",
        "Environment."
    ];

    [Fact]
    public void CoreAssembly_HasNoForbiddenReferences()
    {
        var referenced = typeof(TimingCalculator).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(IsForbiddenAssembly)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void CoreCsproj_HasNoPackageOrProjectReferences()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BuildAnalytics.Core", "BuildAnalytics.Core.csproj"));

        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreSources_LexicalScan_HasNoForbiddenTokens()
    {
        var coreRoot = Path.Combine(RepoRoot(), "src", "BuildAnalytics.Core");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
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

    private static bool IsForbiddenAssembly(string name)
        => name.StartsWith("BuildAnalytics.App", StringComparison.Ordinal)
           || name.StartsWith("System.Net.Http", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal)
           || name.StartsWith("ClosedXML", StringComparison.Ordinal)
           || name.StartsWith("DocumentFormat.OpenXml", StringComparison.Ordinal)
           || name.StartsWith("ExcelNumberFormat", StringComparison.Ordinal)
           || name.StartsWith("SixLabors", StringComparison.Ordinal);

    private static bool IsBuildArtifact(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
           path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepoRoot([CallerFilePath] string callerPath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
