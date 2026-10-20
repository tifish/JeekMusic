using System.Text.Json;
using JeekMusic.Infrastructure;

namespace JeekMusic.Settings;

/// <summary>
/// settings.json 的读写（设计 §14）。<see cref="Current"/> 是唯一的一份设置对象，界面直接改它的属性，
/// 然后调用 <see cref="Save"/>（防抖 1 秒写入）并通过 <see cref="Changed"/> 通知其他部分。只在 UI 线程上修改。
/// </summary>
public sealed class SettingsStore
{
    readonly string _path;
    readonly Timer _timer;
    readonly object _lock = new();

    public AppSettings Current { get; }

    /// <summary>设置变了；参数是变化的类别（如 "rules"、"theme"、"playback"），方便只处理关心的部分。</summary>
    public event Action<string>? Changed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
        Current = Load(_path);
        _timer = new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllBytes(path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Warn("settings", "settings.json 读取失败，使用默认设置", e);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    /// <summary>修改设置后调用。曲库规则改动时传 "rules"，会更新同步用的时间戳。</summary>
    public void Save(string category = "general")
    {
        if (category == "rules") Current.RulesUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _timer.Change(1000, Timeout.Infinite);
        Changed?.Invoke(category);
    }

    /// <summary>立即写入（退出时）。</summary>
    public void WriteNow()
    {
        lock (_lock)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Current, SettingsJsonContext.Default.AppSettings));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (IOException e)
            {
                Log.Warn("settings", "settings.json 写入失败", e);
            }
        }
    }
}
