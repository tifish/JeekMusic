using System.Diagnostics;
using System.Runtime.InteropServices;
using JeekMusic.Library;
using Microsoft.Win32;

namespace JeekMusic.Platform;

/// <summary>当前用户的打开方式注册。默认应用始终由 Windows 的设置界面管理。</summary>
public static partial class FileAssociations
{
    internal const string ProgId = "JeekMusic.Audio";
    internal const string Capabilities = @"Software\JeekMusic\Capabilities";
    internal const string ApplicationKey = @"Software\Classes\Applications\JeekMusic.exe";
    static string Executable => Path.Combine(AppContext.BaseDirectory, "JeekMusic.exe");

    public static bool Registered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApplicationKey + @"\shell\open\command");
            return key?.GetValue("") as string == Command(Executable);
        }
    }

    internal static string Command(string executable) => $"\"{executable}\" \"%1\"";

    public static void Register()
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("找不到 JeekMusic.exe", Executable);
        Register(Registry.CurrentUser, Executable);
        NotifyShell();
    }

    // 接受一个隔离的根键，以便测试注册和注销而不触碰真实文件关联。
    internal static void Register(RegistryKey root, string executable)
    {
        SetDisplayNames(root);
        Set(root, ApplicationKey + @"\shell\open\command", "", Command(executable));
        Set(root, ApplicationKey + @"\DefaultIcon", "", $"\"{executable}\",0");
        Set(root, @"Software\Classes\" + ProgId + @"\shell\open\command", "", Command(executable));
        Set(root, @"Software\Classes\" + ProgId + @"\DefaultIcon", "", $"\"{executable}\",0");
        Set(root, Capabilities, "ApplicationIcon", $"\"{executable}\",0");
        foreach (var ext in Scanner.AudioExtensions)
        {
            Set(root, ApplicationKey + @"\SupportedTypes", ext, "");
            Set(root, Capabilities + @"\FileAssociations", ext, ProgId);
            using var key = root.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
            key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        Set(root, @"Software\RegisteredApplications", "JeekMusic", Capabilities);
    }

    static readonly (string Key, string Name, string Value)[] DisplayNames =
    [
        (ApplicationKey, "FriendlyAppName", "Jeek Music"),
        (@"Software\Classes\" + ProgId, "", "Jeek Music Audio"),
        (Capabilities, "ApplicationName", "Jeek Music"),
        (Capabilities, "ApplicationDescription", "Jeek Music local music player")
    ];

    static void SetDisplayNames(RegistryKey root)
    {
        foreach (var (key, name, value) in DisplayNames) Set(root, key, name, value);
    }

    /// <summary>只刷新当前路径已注册应用的显示文本；不创建注册、不改关联或默认应用。</summary>
    public static void RefreshRegisteredDisplayNames()
    {
        try
        {
            if (!Registered) return;
            var changed = false;
            foreach (var (path, name, value) in DisplayNames)
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(name) as string == value) continue;
                Set(Registry.CurrentUser, path, name, value);
                changed = true;
            }
            if (changed) NotifyShell();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            JeekMusic.Infrastructure.Log.Warn("associations", "无法刷新应用显示名称", e);
        }
    }

    public static void Unregister()
    {
        Unregister(Registry.CurrentUser);
        NotifyShell();
    }

    internal static void Unregister(RegistryKey root)
    {
        foreach (var ext in Scanner.AudioExtensions)
        {
            using var key = root.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", writable: true);
            key?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        root.DeleteSubKeyTree(ApplicationKey, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using var registered = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue("JeekMusic", throwOnMissingValue: false);
    }

    static void Set(RegistryKey root, string path, string name, string value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value);
    }

    public static void OpenDefaults() => Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    static void NotifyShell() => SHChangeNotify(0x08000000, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
