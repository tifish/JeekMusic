using System.Runtime.CompilerServices;

namespace JeekMusic.App.Tests;

internal static class TestDataIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Simulated playback writes normal diagnostic logs too. Never mix them into
        // the real user's logs/settings when developers run dotnet test without an override.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JEEKMUSIC_DATA"))) return;
        var path = Path.Combine(Path.GetTempPath(), "jeekmusic-unit-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("JEEKMUSIC_DATA", path);
    }
}
