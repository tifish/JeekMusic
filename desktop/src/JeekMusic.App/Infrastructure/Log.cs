using System.Collections.Concurrent;
using System.Text;

namespace JeekMusic.Infrastructure;

/// <summary>
/// 简单的文件日志（设计 §4.1）：logs\app.log 和 logs\playback.log（播放日志，同移动端需求 P-14 ③）。
/// 写入在后台线程上合并进行；每个文件超过 5MB 时改名为 .1.log 重新开始。
/// </summary>
public static class Log
{
    const long MaxSize = 5 * 1024 * 1024;
    static readonly BlockingCollection<(string file, string line)> Queue = new();
    static readonly Thread Writer;

    static Log()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "log-writer", Priority = ThreadPriority.BelowNormal };
        Writer.Start();
    }

    public static void Info(string tag, string message) => Enqueue("app", "I", tag, message);

    public static void Warn(string tag, string message, Exception? e = null) =>
        Enqueue("app", "W", tag, e is null ? message : $"{message}: {e}");

    public static void Error(string tag, string message, Exception? e = null) =>
        Enqueue("app", "E", tag, e is null ? message : $"{message}: {e}");

    /// <summary>播放日志：换歌、跳转、暂停、出错、独占模式切换等，方便排查。</summary>
    public static void Playback(string message) => Enqueue("playback", "I", "player", message);

    static void Enqueue(string file, string level, string tag, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{tag}] {message}";
#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
        Queue.Add((file, line));
    }

    static void WriteLoop()
    {
        var pending = new Dictionary<string, StringBuilder>();
        while (true)
        {
            var item = Queue.Take();
            Append(pending, item);
            // 一次把排队的都取出来，合并写入
            while (Queue.TryTake(out var more)) Append(pending, more);
            foreach (var (file, text) in pending)
            {
                if (text.Length == 0) continue;
                try
                {
                    var path = Path.Combine(AppPaths.LogsDir, file + ".log");
                    if (File.Exists(path) && new FileInfo(path).Length > MaxSize)
                        File.Move(path, Path.Combine(AppPaths.LogsDir, file + ".1.log"), overwrite: true);
                    File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                }
                catch (IOException)
                {
                    // 日志写不进去不影响程序
                }
                text.Clear();
            }
        }
    }

    static void Append(Dictionary<string, StringBuilder> pending, (string file, string line) item)
    {
        if (!pending.TryGetValue(item.file, out var sb)) pending[item.file] = sb = new StringBuilder();
        sb.Append(item.line).Append('\n');
    }

    /// <summary>退出前等日志写完（最多 1 秒）。</summary>
    public static void Flush()
    {
        var deadline = Environment.TickCount64 + 1000;
        while (Queue.Count > 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
        Thread.Sleep(20);
    }
}
