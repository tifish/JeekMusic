using System.Globalization;
using Avalonia.Threading;
using JeekMusic.Core.Queue;
using JeekMusic.Library;

namespace JeekMusic.Playback;

/// <summary>
/// 断点续播（需求 P-7，设计 §7.6）：队列存 queue_item 表，当前位置、进度、循环、随机、来源存 kv 表。
/// 队列变化时防抖 1 秒写入；播放中每 10 秒写一次进度；暂停、退出时也写。
/// </summary>
public sealed class QueuePersistence
{
    readonly LibraryDb _db;
    readonly PlayerService _player;
    readonly DispatcherTimer _debounce;
    readonly DispatcherTimer _progress;

    public QueuePersistence(LibraryDb db, PlayerService player)
    {
        _db = db;
        _player = player;
        _debounce = UiTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, () => { _debounce!.Stop(); SaveQueue(); });
        _progress = UiTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, () => SavePosition());
        player.Queue.Changed += () => { _debounce.Stop(); _debounce.Start(); };
        player.StateChanged += () =>
        {
            if (player.IsPlaying) { if (!_progress.IsEnabled) _progress.Start(); }
            else { _progress.Stop(); SavePosition(); }
        };
    }

    public void SaveQueue()
    {
        var q = _player.Queue;
        var rows = q.Items.Select(e => _player.TrackKeyOf(e.TrackId) is { } key ? new QueueItemRow(key, e.Manual) : null).ToList();
        // 找不到 key 的条目（不应该发生）跳过，当前位置相应调整
        var index = q.CurrentIndex - rows.Take(Math.Max(0, q.CurrentIndex)).Count(r => r is null);
        _db.SetQueueItems(rows.Where(r => r is not null).ToList()!);
        _db.SetValue("queue_index", index.ToString(CultureInfo.InvariantCulture));
        _db.SetValue("queue_shuffle", q.Shuffle ? "1" : "0");
        _db.SetValue("queue_repeat", q.Repeat.ToString());
        _db.SetValue("queue_context", q.Context?.Source.Encode());
        _db.SetValue("queue_context_title", q.Context?.Title);
        SavePosition();
    }

    public void SavePosition() =>
        _db.SetValue("queue_position_ms", ((long)_player.Position.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

    /// <summary>启动时恢复（曲库快照已经有了之后）。保存的条目在曲库里找不到的跳过。</summary>
    public void Restore(LibrarySnapshot snapshot)
    {
        var rows = _db.QueueItems();
        if (rows.Count == 0) return;
        var index = int.TryParse(_db.GetValue("queue_index"), out var i) ? i : 0;
        var entries = new List<QueueEntry>();
        var newIndex = 0;
        for (var k = 0; k < rows.Count; k++)
        {
            if (k == index) newIndex = entries.Count;
            if (snapshot.ByKey.TryGetValue(rows[k].TrackKey, out var t)) entries.Add(new QueueEntry(t.Id, rows[k].Manual));
        }
        if (entries.Count == 0) return;
        var context = QueueSource.Decode(_db.GetValue("queue_context")) is { } source ? new QueueContext(source, _db.GetValue("queue_context_title") ?? "") : null;
        var position = long.TryParse(_db.GetValue("queue_position_ms"), out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        _player.Restore(entries, Math.Min(newIndex, entries.Count - 1), _db.GetValue("queue_shuffle") == "1",
            Enum.TryParse<RepeatMode>(_db.GetValue("queue_repeat"), out var r) ? r : RepeatMode.Off, context, position);
    }
}
