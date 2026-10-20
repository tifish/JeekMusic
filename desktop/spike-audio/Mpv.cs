using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace JeekMusic.AudioSpike;

/// <summary>
/// libmpv 的最小封装（C API，直接 P/Invoke，AOT 下不依赖反射）。
/// 事件在单独的线程上用 mpv_wait_event 读取，回调也在那个线程上触发。
/// </summary>
sealed partial class Mpv : IDisposable
{
    const string Lib = "libmpv-2.dll";

    public enum EventId
    {
        None = 0, Shutdown = 1, LogMessage = 2, CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8,
        AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawEvent { public EventId Id; public int Error; public ulong ReplyUserdata; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    struct LogMessage { public IntPtr Prefix; public IntPtr Level; public IntPtr Text; public int LogLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyEvent { public IntPtr Name; public int Format; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    struct EndFileEvent { public int Reason; public int Error; public long PlaylistEntryId; public long PlaylistInsertId; public int PlaylistInsertNumEntries; }

    [LibraryImport(Lib, EntryPoint = "mpv_create")] private static partial IntPtr Create();
    [LibraryImport(Lib, EntryPoint = "mpv_initialize")] private static partial int Initialize(IntPtr h);
    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")] private static partial void TerminateDestroy(IntPtr h);
    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetOptionString(IntPtr h, string name, string value);
    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetPropertyString(IntPtr h, string name, string value);
    [LibraryImport(Lib, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetPropertyString(IntPtr h, string name);
    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ObserveProperty(IntPtr h, ulong userdata, string name, int format);
    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RequestLogMessages(IntPtr h, string minLevel);
    [LibraryImport(Lib, EntryPoint = "mpv_command")] private static partial int Command(IntPtr h, IntPtr args);
    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")] private static partial IntPtr WaitEvent(IntPtr h, double timeout);
    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")] private static partial void Wakeup(IntPtr h);
    [LibraryImport(Lib, EntryPoint = "mpv_free")] private static partial void Free(IntPtr p);
    [LibraryImport(Lib, EntryPoint = "mpv_error_string")] private static partial IntPtr ErrorString(int error);

    IntPtr _handle;
    readonly Thread _eventThread;
    volatile bool _stopping;

    /// <summary>事件回调（事件线程）：事件、属性名（PropertyChange）、属性值的字符串形式。</summary>
    public event Action<EventId, string?, string?>? Notified;
    public event Action<string, string, string>? Log;

    /// <param name="options">mpv_initialize 之前设置的选项</param>
    public Mpv(IEnumerable<(string, string)> options, string? logLevel = null)
    {
        _handle = Create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");
        foreach (var (name, value) in options) Check(SetOptionString(_handle, name, value), $"{name}={value}");
        Check(Initialize(_handle), "mpv_initialize");
        if (logLevel is not null) RequestLogMessages(_handle, logLevel);
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <summary>JeekMusic 用的基础选项：不加载任何配置和脚本、不处理视频和封面、不改动音频。</summary>
    public static List<(string, string)> BaseOptions() =>
    [
        ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
        ("input-default-bindings", "no"), ("osc", "no"), ("idle", "yes"), ("force-window", "no"),
        ("video", "no"), ("audio-display", "no"),        // 内嵌封面也是一路"视频"，不解码
        ("volume", "100"), ("replaygain", "no"),          // 软件音量不是 100 就不再逐位一致
        ("audio-pitch-correction", "no"),
    ];

    public void Observe(string name) => ObserveProperty(_handle, 0, name, 1 /* MPV_FORMAT_STRING */);

    public void Run(params string[] args) => Check(TryRun(args), string.Join(' ', args));

    public int TryRun(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            var array = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, array, ptrs.Length);
                return Command(_handle, array);
            }
            finally { Marshal.FreeHGlobal(array); }
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    public string? Get(string name)
    {
        var p = GetPropertyString(_handle, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { Free(p); }
    }

    public void Set(string name, string value) => Check(SetPropertyString(_handle, name, value), $"{name}={value}");

    void EventLoop()
    {
        while (true)
        {
            var e = Marshal.PtrToStructure<RawEvent>(WaitEvent(_handle, -1));
            if (_stopping) return;
            switch (e.Id)
            {
                case EventId.Shutdown:
                    return;
                case EventId.LogMessage:
                    var log = Marshal.PtrToStructure<LogMessage>(e.Data);
                    Log?.Invoke(Marshal.PtrToStringUTF8(log.Prefix)!, Marshal.PtrToStringUTF8(log.Level)!,
                        Marshal.PtrToStringUTF8(log.Text)!.TrimEnd('\n'));
                    break;
                case EventId.PropertyChange:
                    var prop = Marshal.PtrToStructure<PropertyEvent>(e.Data);
                    string? value = null;
                    if (prop.Format == 1 && prop.Data != IntPtr.Zero) value = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data));
                    Notified?.Invoke(e.Id, Marshal.PtrToStringUTF8(prop.Name), value);
                    break;
                case EventId.EndFile:
                    var end = Marshal.PtrToStructure<EndFileEvent>(e.Data);
                    // reason: 0 播放完 1 停止 2 退出 3 出错 4 重定向；出错时附上原因
                    Notified?.Invoke(e.Id, end.Reason.ToString(), end.Reason == 3 ? ErrorText(end.Error) : null);
                    break;
                case EventId.None:
                    break;
                default:
                    Notified?.Invoke(e.Id, null, null);
                    break;
            }
        }
    }

    static string ErrorText(int error) => Marshal.PtrToStringUTF8(ErrorString(error)) ?? error.ToString();

    static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"mpv: {what} → {ErrorText(result)}");
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        // 不能在另一个线程还在 mpv_wait_event 时销毁句柄：先唤醒事件线程让它退出
        _stopping = true;
        Wakeup(_handle);
        _eventThread.Join(2000);
        TerminateDestroy(_handle);
        _handle = IntPtr.Zero;
    }
}
