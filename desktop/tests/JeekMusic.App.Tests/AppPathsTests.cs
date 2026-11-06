using JeekMusic.Infrastructure;
using Xunit;

namespace JeekMusic.App.Tests;

public sealed class AppPathsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "Jeek portable paths " + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(root);

    [Fact]
    public void NestedRuntimeResolvesPortableRootAndIgnoresWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        Assert.Equal(root, AppPaths.ResolveAppDirectory(Path.Combine(root, "runtime") + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(root, "data"), AppPaths.ResolveDataDirectory(root, null, Path.Combine(root, "legacy")));
        Assert.Equal(root, AppPaths.ResolveAppDirectory(root)); // NativeAOT's flat package.
    }

    [Fact]
    public void ExplicitDataDirectoryAlwaysWinsAndLegacyPackagesStayCompatible()
    {
        var legacy = Path.Combine(root, "legacy");
        var external = Path.Combine(root, "external");
        Assert.Equal(legacy, AppPaths.ResolveDataDirectory(root, null, legacy));
        Assert.Equal(Path.Combine(root, "runtime"), AppPaths.ResolveAppDirectory(Path.Combine(root, "runtime")));
        File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        Assert.Equal(external, AppPaths.ResolveDataDirectory(root, external, legacy));
    }

    public void Dispose() => Directory.Delete(root, true);
}
