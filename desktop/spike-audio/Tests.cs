using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace JeekMusic.AudioSpike;

/// <summary>
/// 自动测试，结果打印到标准输出（key=value）。测试音频由 make-testdata.py 生成。
///   --test-info      libmpv 版本和编译配置（是否 GPL）
///   --test-formats   静音文件分别用 WASAPI 共享 / 独占模式打开，记录解码格式和设备实际格式（不出声）
///   --test-gapless   用 ao=pcm 把输出写成文件，交给 analyze.py 和原信号逐样本比较
///   --test-seek      跳转耗时（发出 seek 到 playback-restart）
///   --test-cpu       实时播放 10 秒的 CPU 占用和内存
///   --data &lt;目录&gt;     testdata/ 的位置（默认从程序所在目录往上找）
/// </summary>
static class Tests
{
    const string LibraryDir = @"D:\Mobile\Song\散曲";

    public static int Run(string[] args)
    {
        var dataArg = Array.IndexOf(args, "--data");
        var data = dataArg >= 0 ? args[dataArg + 1] : FindTestData();
        if (args.Contains("--test-info")) Info();
        if (args.Contains("--test-formats")) Formats(data);
        if (args.Contains("--test-gapless")) Gapless(data);
        if (args.Contains("--test-seek")) Seek(data);
        if (args.Contains("--test-cpu")) Cpu(data);
        return 0;
    }

    static void Out(string key, object? value) => Console.WriteLine($"{key}={value}");

    static string FindTestData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "testdata");
            if (File.Exists(Path.Combine(dir.FullName, "make-testdata.py")) && Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("找不到 testdata/，先运行 python make-testdata.py");
    }

    static string? LibraryMp3() =>
        Directory.Exists(LibraryDir) ? Directory.EnumerateFiles(LibraryDir, "*.mp3").FirstOrDefault() : null;

    /// <summary>把 mpv 的事件转到队列里，测试线程按顺序等待。</summary>
    sealed class Session : IDisposable
    {
        public readonly Mpv Mpv;
        readonly BlockingCollection<(Mpv.EventId id, string? name, string? value)> _events = new();
        public readonly List<string> Logs = new();

        public Session(IEnumerable<(string, string)> extraOptions, string? logLevel = null)
        {
            Mpv = new Mpv(Mpv.BaseOptions().Concat(extraOptions), logLevel);
            Mpv.Notified += (id, name, value) => _events.Add((id, name, value));
            Mpv.Log += (prefix, level, text) => { lock (Logs) Logs.Add($"[{prefix}] {text}"); };
        }

        public void WaitFor(Func<(Mpv.EventId id, string? name, string? value), bool> match, int timeoutMs = 10000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var left = (int)(deadline - Environment.TickCount64);
                if (left <= 0 || !_events.TryTake(out var e, left)) throw new TimeoutException("等待 mpv 事件超时");
                if (e.id == Mpv.EventId.EndFile && e.name == "3") throw new InvalidOperationException("播放出错：" + e.value);
                if (match(e)) return;
            }
        }

        public void WaitFor(Mpv.EventId id, int timeoutMs = 10000) => WaitFor(e => e.id == id, timeoutMs);

        public void Dispose() => Mpv.Dispose();
    }

    static void Info()
    {
        using var s = new Session([]);
        Out("mpv_version", s.Mpv.Get("mpv-version"));
        Out("ffmpeg_version", s.Mpv.Get("ffmpeg-version"));
        Out("mpv_configuration", s.Mpv.Get("mpv-configuration"));
    }

    static void Formats(string data)
    {
        var files = Directory.GetFiles(Path.Combine(data, "formats"), "*.flac")
            .OrderBy(f => int.Parse(Path.GetFileName(f).Split('-')[1].Split('k')[0].Split('.')[0]));
        foreach (var file in files)
        foreach (var exclusive in new[] { false, true })
        {
            var tag = $"{Path.GetFileNameWithoutExtension(file)}.{(exclusive ? "exclusive" : "shared")}";
            try
            {
                using var s = new Session([("ao", "wasapi"), ("audio-exclusive", exclusive ? "yes" : "no")], "v");
                s.Mpv.Run("loadfile", file);
                s.WaitFor(Mpv.EventId.PlaybackRestart);
                Thread.Sleep(500);
                Out(tag + ".decoded", $"{s.Mpv.Get("audio-params/samplerate")} {s.Mpv.Get("audio-params/format")}");
                Out(tag + ".device", $"{s.Mpv.Get("audio-out-params/samplerate")} {s.Mpv.Get("audio-out-params/format")} "
                    + $"{s.Mpv.Get("audio-out-params/channel-count")}ch via {s.Mpv.Get("current-ao")}");
                lock (s.Logs)
                    foreach (var line in s.Logs.Where(l => l.StartsWith("[ao/wasapi]")).Take(12))
                        Out(tag + ".log", line);
            }
            catch (Exception ex)
            {
                Out(tag + ".error", ex.Message);
            }
        }
    }

    /// <summary>
    /// ao=pcm 把送给"设备"的样本原样写进文件（不按实时速度）。追加模式不能写 WAV 头，格式另存到 .fmt。
    /// 测的是解码层面的无缝和逐位一致：编码器延迟和补齐有没有裁对、CUE 分轨的起止是否精确到样本、
    /// 中间有没有改动样本。设备层面（换歌时是否重开设备）看 gapless-audio 的语义，再用耳朵听。
    /// </summary>
    static void Gapless(string data)
    {
        var g = Path.Combine(data, "gapless");
        var album = Path.Combine(data, "cue", "album.flac");
        var outDir = Path.Combine(data, "out");
        Directory.CreateDirectory(outDir);
        (string, string?)[] Parts(string ext) => Enumerable.Range(1, 3).Select(i => (Path.Combine(g, $"part{i}.{ext}"), (string?)null)).ToArray();
        (string, string?)[] cue = [(album, "start=0,end=6"), (album, "start=6,end=13.4"), (album, "start=13.4")];
        // end 比分轨点早 1 微秒（小于 192kHz 的半个样本）：13.4 不能用浮点数精确表示，分轨点上的样本会前后两轨各放一次
        (string, string?)[] cueFixed = [(album, "start=0,end=5.999999"), (album, "start=6,end=13.399999"), (album, "start=13.4")];
        var cases = new (string name, string gapless, (string file, string? opts)[] items)[]
        {
            ("flac-yes", "yes", Parts("flac")),
            ("flac-weak", "weak", Parts("flac")),
            ("mp3-yes", "yes", Parts("mp3")),
            ("mp3-weak", "weak", Parts("mp3")),
            ("cue-yes", "yes", cue),
            ("cue-weak", "weak", cue),
            ("cue-fixed", "yes", cueFixed),
        };
        foreach (var (name, gapless, items) in cases)
        {
            var raw = Path.Combine(outDir, name + ".raw");
            File.Delete(raw);
            try
            {
                using var s = new Session([
                    ("ao", "pcm"), ("ao-pcm-file", raw), ("ao-pcm-waveheader", "no"), ("ao-pcm-append", "yes"),
                    ("gapless-audio", gapless)]);
                s.Mpv.Observe("idle-active");
                s.Mpv.Observe("audio-out-params/format");
                var first = true;
                foreach (var (file, opts) in items)
                {
                    var flag = first ? "append-play" : "append";
                    if (opts is null) s.Mpv.Run("loadfile", file, flag);
                    else s.Mpv.Run("loadfile", file, flag, "-1", opts);
                    first = false;
                }
                string? format = null;
                var started = false;
                s.WaitFor(e =>
                {
                    if (e.id == Mpv.EventId.StartFile) started = true;
                    if (e.id == Mpv.EventId.PropertyChange && e.name == "audio-out-params/format" && e.value is not null) format = e.value;
                    return started && e.id == Mpv.EventId.PropertyChange && e.name == "idle-active" && e.value == "yes";
                }, 60000);
                File.WriteAllText(Path.ChangeExtension(raw, ".fmt"), format);
                Out(name, $"{format} {new FileInfo(raw).Length} bytes");
            }
            catch (Exception ex)
            {
                Out(name + ".error", ex.Message);
            }
        }
    }

    static void Seek(string data)
    {
        var cases = new List<(string name, string file, bool exclusive)>
        {
            ("flac96k-null", Path.Combine(data, "load", "noise-96k-24.flac"), false),
        };
        if (LibraryMp3() is { } mp3) cases.Add(("mp3-null", mp3, false));
        cases.Add(("flac96k-exclusive-silent", Path.Combine(data, "formats", "silence-96k-24.flac"), true));

        var rnd = new Random(1);
        foreach (var (name, file, exclusive) in cases)
        {
            try
            {
                using var s = new Session([("ao", exclusive ? "wasapi" : "null"), ("audio-exclusive", exclusive ? "yes" : "no")]);
                s.Mpv.Run("loadfile", file);
                s.WaitFor(Mpv.EventId.PlaybackRestart);
                var length = double.Parse(s.Mpv.Get("duration") ?? "3", CultureInfo.InvariantCulture);
                var times = new List<double>();
                for (var i = 0; i < 12; i++)
                {
                    var target = rnd.NextDouble() * (length - 1);
                    var sw = Stopwatch.StartNew();
                    s.Mpv.Run("seek", target.ToString("F3", CultureInfo.InvariantCulture), "absolute");
                    s.WaitFor(Mpv.EventId.PlaybackRestart);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                Out(name, $"median {times[times.Count / 2]:F1} ms, max {times[^1]:F1} ms ({Path.GetFileName(file)})");
            }
            catch (Exception ex)
            {
                Out(name + ".error", ex.Message);
            }
        }
    }

    static void Cpu(string data)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        Out("before_mpv_working_set_mb", self.WorkingSet64 / 1048576);
        var noise = Path.Combine(data, "load", "noise-96k-24.flac");
        var silence = Path.Combine(data, "long", "silence-96k-24-60s.flac");
        // ao=null 按实时速度"播放"、不出声，只算解码；静音文件走真实的 WASAPI，算上设备输出
        var files = new List<(string, string, (string, string)[])>
        {
            ("flac96k24-noise.null", noise, [("ao", "null")]),
            ("flac96k24-silence.wasapi-shared", silence, [("ao", "wasapi")]),
            ("flac96k24-silence.wasapi-exclusive", silence, [("ao", "wasapi"), ("audio-exclusive", "yes")]),
        };
        if (LibraryMp3() is { } mp3) files.Add(("mp3.null", mp3, [("ao", "null")]));
        foreach (var (name, file, options) in files)
        {
            using var s = new Session(options);
            s.Mpv.Run("loadfile", file);
            s.WaitFor(Mpv.EventId.PlaybackRestart);
            self.Refresh();
            var cpu0 = self.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            Thread.Sleep(10000);
            self.Refresh();
            var cpu = (self.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds;
            Out(name, $"cpu {cpu * 100:F2}% of one core, working set {self.WorkingSet64 / 1048576} MB, private {self.PrivateMemorySize64 / 1048576} MB");
        }
    }
}
