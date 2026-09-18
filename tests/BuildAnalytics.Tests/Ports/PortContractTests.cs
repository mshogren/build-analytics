using System.Reflection;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Ports;

public sealed class PortContractTests
{
    [Fact]
    public void Ports_ExposeExactAsyncSignatures()
    {
        AssertMethod(typeof(IBuildSource), "ListAsync", typeof(Task<BuildPage>), typeof(BuildQuery), typeof(string), typeof(CancellationToken));
        AssertMethod(typeof(IBuildSource), "GetDetailAsync", typeof(Task<BuildRun>), typeof(BuildQuery), typeof(int), typeof(CancellationToken));
        AssertMethod(typeof(IManifestStore), "TryReadAsync", typeof(Task<Manifest>), typeof(CancellationToken));
        AssertMethod(typeof(IManifestStore), "CommitAsync", typeof(Task), typeof(Manifest), typeof(CancellationToken));
        AssertMethod(typeof(IRunStore), "WriteAsync", typeof(Task), typeof(BuildRun), typeof(CancellationToken));
        AssertMethod(typeof(IRunStore), "TryReadAsync", typeof(Task<BuildRun>), typeof(int), typeof(CancellationToken));
        AssertMethod(typeof(IRunStore), "ListRunIdsAsync", typeof(Task<IReadOnlyList<int>>), typeof(CancellationToken));
        AssertMethod(typeof(ITimingReportWriter), "WriteAsync", typeof(Task), typeof(TimingSummary), typeof(CancellationToken));
        AssertMethod(typeof(IDelayScheduler), "DelayAsync", typeof(Task), typeof(TimeSpan), typeof(CancellationToken));
        AssertMethod(typeof(IDefinitionResolver), "ResolveAsync", typeof(Task<IReadOnlyList<int>>), typeof(BuildQuery), typeof(IReadOnlyList<string>), typeof(CancellationToken));
    }

    [Fact]
    public void IBuildSource_DoesNotExposeIAsyncEnumerable()
    {
        var methods = typeof(IBuildSource).GetMethods();

        Assert.DoesNotContain(
            methods,
            method => method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
    }

    [Fact]
    public void Ports_NoSynchronousIoMethods()
    {
        foreach (var port in PortTypes())
        {
            foreach (var method in port.GetMethods())
            {
                Assert.True(
                    typeof(Task).IsAssignableFrom(method.ReturnType),
                    $"{port.Name}.{method.Name} must return Task/Task<T>");
            }
        }
    }

    private static IEnumerable<Type> PortTypes()
    {
        yield return typeof(IBuildSource);
        yield return typeof(IManifestStore);
        yield return typeof(IRunStore);
        yield return typeof(ITimingReportWriter);
        yield return typeof(IDelayScheduler);
        yield return typeof(IDefinitionResolver);
    }

    private static void AssertMethod(Type type, string name, Type returnType, params Type[] parameterTypes)
    {
        var method = type.GetMethod(name, parameterTypes);

        Assert.NotNull(method);
        Assert.Equal(returnType, method!.ReturnType);
    }
}
