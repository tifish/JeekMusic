namespace JeekMusic.Infrastructure;

/// <summary>程序的数据目录 %LOCALAPPDATA%\JeekMusic（设计 §4.1）。测试时可以用 JEEKMUSIC_DATA 环境变量换到别处。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Init();

    static string Init()
    {
        var dir = Environment.GetEnvironmentVariable("JEEKMUSIC_DATA");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JeekMusic");
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

    /// <summary>程序所在目录（libmpv、模型文件都在这里）。</summary>
    public static string AppDir { get; } = AppContext.BaseDirectory;

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
