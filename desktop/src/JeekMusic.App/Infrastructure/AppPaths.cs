namespace JeekMusic.Infrastructure;

/// <summary>绿色包的数据保存在程序旁的 data/；JEEKMUSIC_DATA 始终优先。旧平铺版本兼容本地应用数据。</summary>
public static class AppPaths
{
    public static string AppDir { get; } = ResolveAppDirectory(AppContext.BaseDirectory);
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppDir, "portable.json"));
    public static string DataDirectorySource => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JEEKMUSIC_DATA"))
        ? "environment" : IsPortable ? "portable" : "localAppData";
    public static string DataDir { get; } = Init();

    internal static string ResolveAppDirectory(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var parent = Path.GetDirectoryName(directory);
        return Path.GetFileName(directory).Equals("runtime", StringComparison.OrdinalIgnoreCase) &&
            parent is not null && File.Exists(Path.Combine(parent, "portable.json")) ? parent : directory;
    }

    internal static string ResolveDataDirectory(string appDirectory, string? configuredDirectory, string legacyDirectory) =>
        Path.GetFullPath(!string.IsNullOrEmpty(configuredDirectory) ? configuredDirectory :
            File.Exists(Path.Combine(appDirectory, "portable.json")) ? Path.Combine(appDirectory, "data") : legacyDirectory);

    static string Init()
    {
        var dir = ResolveDataDirectory(AppDir, Environment.GetEnvironmentVariable("JEEKMUSIC_DATA"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JeekMusic"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Database => Path.Combine(DataDir, "library.db");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Snapshot => Path.Combine(DataDir, "snapshot.json");
    public static string Device => Path.Combine(DataDir, "device.json");
    public static string CoversDir => Ensure(Path.Combine(DataDir, "covers"));
    public static string LyricsCacheDir => Ensure(Path.Combine(DataDir, "lyrics"));
    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string UpdateDir => Path.Combine(DataDir, "update");

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
