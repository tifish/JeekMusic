using System.Runtime.InteropServices;

namespace JeekMusic.Platform;

/// <summary>用到的 Win32 API（LibraryImport，AOT 下不依赖运行时生成的封送代码）。</summary>
public static partial class Win32
{
    // ── 卷 ──

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string volumeName, [Out] char[] pathNames, int bufferLength, out int returnLength);

    // ── 窗口 ──

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    public const int ASFW_ANY = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    // ── 快捷键 ──

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    // ── 电源、全屏 ──

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013, PBT_APMRESUMEAUTOMATIC = 0x12;
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");

    [LibraryImport("user32.dll")]
    public static partial nint RegisterPowerSettingNotification(nint recipient, in Guid powerSettingGuid, int flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public int Data;
    }

    /// <summary>QUNS_BUSY = 2（全屏程序）、QUNS_RUNNING_D3D_FULL_SCREEN = 3、QUNS_PRESENTATION_MODE = 4。</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    // ── 设备变化 ──

    public const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;

    // ── 线程优先级 ──

    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000, THREAD_MODE_BACKGROUND_END = 0x00020000;

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadPriority(nint thread, int priority);

    // ── 回收站 ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ref SHFILEOPSTRUCT op);

    // ── 按流量计费的网络 ──

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();
}
