using System.Globalization;
using System.Runtime.InteropServices;

namespace JeekMusic.Playback;

/// <summary>
/// libmpv 的封装（C API，P/Invoke；验证工程 spike-audio/Mpv.cs 发展而来，设计 §7.1）。
/// 事件在单独的线程上用 mpv_wait_event 读取，回调也在那个线程上触发，使用者自己转到 UI 线程。
/// </summary>
public sealed partial class Mpv : IDisposable
{
    const string Lib = "libmpv-2.dll";

    public enum EventId
    {
        None = 0, Shutdown = 1, LogMessage = 2, CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8,
        AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22,
    }

    /// <summary>mpv_end_file_reason。</summary>
    public enum EndReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

    [StructLayout(LayoutKind.Sequential)]
    struct RawEvent { public EventId Id; public int Error; public ulong ReplyUserdata; public nint Data; }

    [StructLayout(LayoutKind.Sequential)]
    struct LogMessage { public nint Prefix; public nint Level; public nint Text; public int LogLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyEvent { public nint Name; public int Format; public nint Data; }

    [StructLayout(LayoutKind.Sequential)]
    struct StartFileEvent { public long PlaylistEntryId; }

    [StructLayout(LayoutKind.Sequential)]
    struct EndFileEvent { public int Reason; public int Error; public long PlaylistEntryId; public long PlaylistInsertId; public int PlaylistInsertNumEntries; }

    [LibraryImport(Lib, EntryPoint = "mpv_create")] private static partial nint Create();
    [LibraryImport(Lib, EntryPoint = "mpv_initialize")] private static partial int Initialize(nint h);
    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")] private static partial void TerminateDestroy(nint h);
    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetOptionString(nint h, string name, string value);
    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetPropertyString(nint h, string name, string value);
    [LibraryImport(Lib, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetPropertyString(nint h, string name);
    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ObserveProperty(nint h, ulong userdata, string name, int format);
    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RequestLogMessages(nint h, string minLevel);
    [LibraryImport(Lib, EntryPoint = "mpv_command")] private static partial int Command(nint h, nint args);
    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")] private static partial nint WaitEvent(nint h, double timeout);
    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")] private static partial void Wakeup(nint h);
    [LibraryImport(Lib, EntryPoint = "mpv_free")] private static partial void Free(nint p);
    [LibraryImport(Lib, EntryPoint = "mpv_error_string")] private static partial nint ErrorString(int error);

    nint _handle;
    readonly Thread _eventThread;
    volatile bool _stopping;

    /// <summary>属性变化（事件线程）：属性名、字符串形式的值（null 表示属性不可用）。</summary>
    public event Action<string, string?>? PropertyChanged;
    /// <summary>开始播放播放列表里的某一条（事件线程）。</summary>
    public event Action<long>? FileStarted;
    /// <summary>某一条结束（事件线程）：条目 ID、原因、出错时的错误文字。</summary>
    public event Action<long, EndReason, string?>? FileEnded;
    /// <summary>其他事件（PlaybackRestart、AudioReconfig、FileLoaded、Seek）。</summary>
    public event Action<EventId>? Notified;
    public event Action<string, string, string>? Log;

    public Mpv(IEnumerable<(string, string)> options, string? logLevel = null)
    {
        _handle = Create();
        if (_handle == 0) throw new InvalidOperationException("mpv_create 失败");
        try
        {
            foreach (var (name, value) in options)
            {
                var result = SetOptionString(_handle, name, value);
                // Minimal LGPL builds omit the scripting subsystem, including its disable options.
                if (result == -5 && value == "no" && name is "load-scripts" or "ytdl" or "osc") continue;
                Check(result, $"{name}={value}");
            }
            Check(Initialize(_handle), "mpv_initialize");
        }
        catch { TerminateDestroy(_handle); _handle = 0; throw; }
        if (logLevel is not null) RequestLogMessages(_handle, logLevel);
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <summary>
    /// 基础选项（验证结论，tech-spike.md 第二部分）：不加载任何配置和脚本、不处理视频和封面、不改动音频。
    /// </summary>
    public static List<(string, string)> BaseOptions() =>
    [
        ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
        ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("osc", "no"), ("idle", "yes"), ("force-window", "no"),
        ("video", "no"), ("audio-display", "no"),        // 内嵌封面也是一路"视频"，不解码
        ("volume", "100"), ("replaygain", "no"),          // 软件音量不是 100 就不再逐位一致
        ("audio-pitch-correction", "no"),
        ("sub-auto", "no"), ("audio-file-auto", "no"), ("cover-art-auto", "no"),
    ];

    public void Observe(string name) => ObserveProperty(_handle, 0, name, 1 /* MPV_FORMAT_STRING */);

    public void Run(params string[] args) => Check(TryRun(args), string.Join(' ', args));

    public int TryRun(params string[] args)
    {
        if (_handle == 0) return -1;
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            var array = Marshal.AllocHGlobal(nint.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, array, ptrs.Length);
                return Command(_handle, array);
            }
            finally { Marshal.FreeHGlobal(array); }
        }
        finally
        {
            foreach (var p in ptrs) if (p != 0) Marshal.FreeCoTaskMem(p);
        }
    }

    public string? Get(string name)
    {
        if (_handle == 0) return null;
        var p = GetPropertyString(_handle, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { Free(p); }
    }

    public double? GetDouble(string name) =>
        double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public long? GetLong(string name) =>
        long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Set(string name, string value) => Check(TrySet(name, value), $"{name}={value}");

    public int TrySet(string name, string value) => _handle == 0 ? -1 : SetPropertyString(_handle, name, value);

    public static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    void EventLoop()
    {
        while (true)
        {
            var e = Marshal.PtrToStructure<RawEvent>(WaitEvent(_handle, -1));
            if (_stopping) return;
            try
            {
                switch (e.Id)
                {
                    case EventId.Shutdown:
                        return;
                    case EventId.LogMessage:
                        var log = Marshal.PtrToStructure<LogMessage>(e.Data);
                        Log?.Invoke(Marshal.PtrToStringUTF8(log.Prefix)!, Marshal.PtrToStringUTF8(log.Level)!, Marshal.PtrToStringUTF8(log.Text)!.TrimEnd('\n'));
                        break;
                    case EventId.PropertyChange:
                        var prop = Marshal.PtrToStructure<PropertyEvent>(e.Data);
                        string? value = null;
                        if (prop.Format == 1 && prop.Data != 0) value = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data));
                        PropertyChanged?.Invoke(Marshal.PtrToStringUTF8(prop.Name)!, value);
                        break;
                    case EventId.StartFile:
                        FileStarted?.Invoke(Marshal.PtrToStructure<StartFileEvent>(e.Data).PlaylistEntryId);
                        break;
                    case EventId.EndFile:
                        var end = Marshal.PtrToStructure<EndFileEvent>(e.Data);
                        // mpv 的 reason：0 EOF、2 STOP、3 QUIT、4 ERROR、5 REDIRECT
                        FileEnded?.Invoke(end.PlaylistEntryId, (EndReason)end.Reason, end.Reason == 4 ? ErrorText(end.Error) : null);
                        break;
                    case EventId.None:
                        break;
                    default:
                        Notified?.Invoke(e.Id);
                        break;
                }
            }
            catch (Exception ex)
            {
                Infrastructure.Log.Error("mpv", "事件处理出错", ex);
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
        if (_handle == 0) return;
        // 不能在另一个线程还在 mpv_wait_event 时销毁句柄：先唤醒事件线程让它退出
        _stopping = true;
        Wakeup(_handle);
        _eventThread.Join();
        TerminateDestroy(_handle);
        _handle = 0;
    }
}
