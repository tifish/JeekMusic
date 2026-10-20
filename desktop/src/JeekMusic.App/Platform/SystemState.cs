using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace JeekMusic.Platform;

/// <summary>
/// 电源、全屏、设备插拔（设计 §10.3、§5.2）：用系统通知，不轮询。
/// </summary>
public sealed class SystemState
{
    /// <summary>接上 / 拔掉电源。</summary>
    public event Action? PowerChanged;
    /// <summary>有盘插上或拔掉（WM_DEVICECHANGE）。</summary>
    public event Action? VolumesChanged;
    /// <summary>从睡眠中恢复。</summary>
    public event Action? Resumed;

    public void Attach(WindowMessages messages)
    {
        Win32.RegisterPowerSettingNotification(messages.Hwnd, Win32.GUID_ACDC_POWER_SOURCE, 0);
        messages.Add((_, msg, wParam, lParam) =>
        {
            switch (msg)
            {
                case Win32.WM_POWERBROADCAST when wParam == Win32.PBT_POWERSETTINGCHANGE:
                    PowerChanged?.Invoke();
                    return false;
                case Win32.WM_POWERBROADCAST when wParam == Win32.PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    return false;
                case Win32.WM_DEVICECHANGE when wParam is Win32.DBT_DEVICEARRIVAL or Win32.DBT_DEVICEREMOVECOMPLETE:
                    VolumesChanged?.Invoke();
                    return false;
            }
            return false;
        });
    }

    /// <summary>接着电源；台式机（没有电池）视为一直接着。</summary>
    public static bool OnAcPower
    {
        get
        {
            if (!Win32.GetSystemPowerStatus(out var s)) return true;
            if (s.BatteryFlag == 128) return true; // 没有电池
            return s.ACLineStatus != 0;
        }
    }

    /// <summary>有程序在全屏（游戏、视频、演示）。</summary>
    public static bool FullscreenAppRunning =>
        Win32.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;

    /// <summary>移到回收站（需求 F-10）；路径可以有多个。成功返回 true。</summary>
    public static bool MoveToRecycleBin(IReadOnlyList<string> paths, nint owner)
    {
        if (paths.Count == 0) return true;
        var from = string.Join('\0', paths) + "\0\0";
        var ptr = Marshal.StringToHGlobalUni(from);
        try
        {
            var op = new Win32.SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = Win32.FO_DELETE,
                pFrom = ptr,
                fFlags = (ushort)(Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI),
            };
            return Win32.SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>让电脑睡眠（睡眠定时"停止后让电脑睡眠"，需求 O-8）。</summary>
    public static void Sleep() => Win32.SetSuspendState(false, false, false);

    /// <summary>当前线程进入后台模式（CPU 和磁盘 IO 都是最低优先级，设计 §10.3）。</summary>
    public static void BeginBackgroundThread() => Win32.SetThreadPriority(Win32.GetCurrentThread(), Win32.THREAD_MODE_BACKGROUND_BEGIN);

    public static void EndBackgroundThread() => Win32.SetThreadPriority(Win32.GetCurrentThread(), Win32.THREAD_MODE_BACKGROUND_END);

    // ── 开机启动（设置 → 窗口）──

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetLaunchAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled) key.SetValue("JeekMusic", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("JeekMusic", throwOnMissingValue: false);
    }

    /// <summary>按流量计费的网络（在线歌词、封面"仅不计流量的网络下自动"）。</summary>
    public static bool IsMeteredNetwork
    {
        get
        {
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                var cost = profile?.GetConnectionCost();
                return cost is not null && cost.NetworkCostType is Windows.Networking.Connectivity.NetworkCostType.Fixed or Windows.Networking.Connectivity.NetworkCostType.Variable;
            }
            catch (Exception) { return false; }
        }
    }

    public static bool IsOnline
    {
        get
        {
            try { return Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile() is not null; }
            catch (Exception) { return true; }
        }
    }
}
