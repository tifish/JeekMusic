using JeekMusic.Library;

namespace JeekMusic.Online;

/// <summary>批量写入封面、歌词后合并重建曲库快照：2 秒内的多次写入只重建一次。</summary>
public static class LibraryRefresh
{
    static Timer? _timer;
    static bool _stopped;
    static LibraryService? _library;
    static readonly Lock Gate = new();

    public static void Stop()
    {
        lock (Gate) { _stopped = true; _timer?.Dispose(); _timer = null; _library = null; }
    }

    public static void Soon(LibraryService library)
    {
        lock (Gate)
        {
            if (_stopped) return;
            _library = library;
            _timer ??= new Timer(_ => _library?.RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(2000, Timeout.Infinite);
        }
    }
}
