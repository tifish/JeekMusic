namespace JeekMusic.Core.Queue;

/// <summary>队列里的一条：歌曲 id，以及是不是手动添加的（"下一首播放""添加到队列"）。</summary>
public readonly record struct QueueEntry(long TrackId, bool Manual);

public enum RepeatMode { Off, All, One }

/// <summary>队列来自的列表和它的显示名称（如文件夹名、艺术家名）。</summary>
public sealed record QueueContext(QueueSource Source, string Title);

/// <summary>
/// 播放队列（需求 P-6、P-11、P-15；规则同移动端 playback/QueueController.kt，桌面端设计 §7.2）。
/// 队列分两部分：
/// <list type="bullet">
/// <item><b>手动添加的</b>：紧跟在当前歌曲后面，先播完它们；</item>
/// <item><b>来自列表的</b>：记住来自哪个列表（<see cref="Context"/>），列表变了（筛选、排序、曲库）就用
/// <see cref="SourceChanged"/> 按新列表重新生成，当前这首不打断，手动添加的不受影响。</item>
/// </list>
/// 随机播放是把队列本身排成随机顺序，"随机"只是个标记：队列里看到的就是实际的播放顺序。
/// 这个类只管顺序，不碰播放器；播放器（mpv）只拿"当前这首"和"下一首"（<see cref="NextIndex"/>）。
/// 不是线程安全的，只在 UI 线程上用。
/// </summary>
public sealed class PlayQueue
{
    readonly Random _random;
    readonly List<QueueEntry> _items = new();
    IReadOnlyList<long>? _sourceList;
    // A cross-source current song absent from the new source remains reachable as history,
    // but must not reappear when Next/Repeat wraps the new list.
    public int SourceAnchorIndex { get; private set; } = -1;

    public PlayQueue(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<QueueEntry> Items => _items;

    /// <summary>当前歌曲的位置；队列为空时是 -1。</summary>
    public int CurrentIndex { get; private set; } = -1;

    public QueueEntry? Current => CurrentIndex >= 0 ? _items[CurrentIndex] : null;

    public bool Shuffle { get; private set; }

    public RepeatMode Repeat { get; set; }

    /// <summary>队列来自的列表；只用"下一首播放"建立的队列、或来源已经不存在时为 null，这时不重新生成。</summary>
    public QueueContext? Context { get; private set; }

    /// <summary>队列的内容或当前位置变了。</summary>
    public event Action? Changed;

    /// <summary>
    /// 播放 <paramref name="list"/>（来自 <paramref name="context"/> 的列表，按列表顺序），从 <paramref name="startIndex"/> 开始；
    /// 为 null 时从头开始，随机时随机挑一首。<paramref name="shuffle"/> 为 null 时保持当前的随机模式。
    /// 手动添加、还没播的歌保留在当前歌曲后面。
    /// </summary>
    public void Play(QueueContext? context, IReadOnlyList<long> list, int? startIndex, bool? shuffle = null)
    {
        if (list.Count == 0) return;
        var random = shuffle ?? Shuffle;
        var start = startIndex is { } s ? Math.Clamp(s, 0, list.Count - 1) : random ? _random.Next(list.Count) : 0;
        List<long> order;
        if (random)
        {
            var rest = list.Where((_, i) => i != start).ToList();
            ShuffleInPlace(rest);
            order = [list[start], .. rest];
        }
        else order = [.. list];
        var current = random ? 0 : start;
        var manual = ManualAfterCurrent();

        _items.Clear();
        _items.AddRange(order.Select(id => new QueueEntry(id, false)));
        _items.InsertRange(current + 1, manual);
        CurrentIndex = current;
        SourceAnchorIndex = -1;
        Shuffle = random;
        Context = context;
        _sourceList = context is null ? null : list;
        Changed?.Invoke();
    }

    /// <summary>"下一首播放"：插到当前歌曲后面。队列为空时直接播放这些歌（没有来源）。</summary>
    public void PlayNext(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        if (_items.Count == 0) { PlayWithoutSource(ids); return; }
        InsertManual(CurrentIndex + 1, ids);
        Changed?.Invoke();
    }

    /// <summary>"添加到队列"：加到手动添加的歌的最后，也就是来自列表的歌前面。</summary>
    public void AddToQueue(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        if (_items.Count == 0) { PlayWithoutSource(ids); return; }
        InsertManual(CurrentIndex + 1 + ManualAfterCurrent().Count, ids);
        Changed?.Invoke();
    }

    void PlayWithoutSource(IReadOnlyList<long> ids)
    {
        _items.Clear();
        _items.AddRange(ids.Select(id => new QueueEntry(id, false)));
        CurrentIndex = 0;
        SourceAnchorIndex = -1;
        Context = null;
        _sourceList = null;
        Changed?.Invoke();
    }

    void InsertManual(int index, IReadOnlyList<long> ids)
    {
        if (SourceAnchorIndex >= index) SourceAnchorIndex += ids.Count;
        _items.InsertRange(index, ids.Select(id => new QueueEntry(id, true)));
    }

    /// <summary>断点续播：恢复保存的队列（已经是播放顺序，随机时也不重新排）。来源的列表等下次 <see cref="SourceChanged"/> 再记下。</summary>
    public void Restore(IReadOnlyList<QueueEntry> items, int currentIndex, bool shuffle, RepeatMode repeat, QueueContext? context, int sourceAnchorIndex = -1)
    {
        _items.Clear();
        _items.AddRange(items);
        CurrentIndex = items.Count == 0 ? -1 : Math.Clamp(currentIndex, 0, items.Count - 1);
        SourceAnchorIndex = sourceAnchorIndex >= 0 && sourceAnchorIndex < items.Count ? sourceAnchorIndex : -1;
        Shuffle = shuffle;
        Repeat = repeat;
        Context = context;
        _sourceList = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// 来源列表的最新内容（按列表顺序）。第一次（断点续播后）只记下来；和上次不同时按新列表重新生成来自列表的部分。
    /// </summary>
    public void SourceChanged(IReadOnlyList<long> list)
    {
        if (Context is null) return;
        var previous = _sourceList;
        _sourceList = list;
        if (SourceAnchorIndex >= 0 && list.Contains(_items[SourceAnchorIndex].TrackId))
        {
            SourceAnchorIndex = -1;
            Changed?.Invoke();
        }
        if (previous is null || previous.SequenceEqual(list) || _items.Count == 0) return;
        // 随机时已经播过的歌（还在新列表里的）保留在前面，这一轮不再播
        var history = Shuffle ? _items.Take(CurrentIndex).Where(e => !e.Manual).Select(e => e.TrackId).ToList() : null;
        Apply(list, history);
    }

    /// <summary>显式选择当前视图的来源/排序筛选，保留当前条目和待播手动项，不跳转或开始播放。
    /// 顺序模式从当前曲在新列表的位置接续；不在新列表时从首曲接续。
    /// 跨来源不继承旧列表的顺序/随机历史，同源内容更新仍走 SourceChanged。</summary>
    public void SwitchSource(QueueContext context, IReadOnlyList<long> list)
    {
        if (Current is not { } current) return;
        if (Context?.Source == context.Source && _sourceList?.SequenceEqual(list) == true) { Context = context; return; }
        var manual = _items.Skip(CurrentIndex + 1).Where(e => e.Manual).ToList();
        var here = IndexOf(list, current.TrackId);
        List<long> before, after;
        if (Shuffle)
        {
            before = [];
            after = list.Where((_, index) => index != here).ToList();
            ShuffleInPlace(after);
        }
        else
        {
            before = here >= 0 ? list.Take(here).ToList() : [];
            after = here >= 0 ? list.Skip(here + 1).ToList() : [.. list];
        }
        _items.Clear();
        _items.AddRange(before.Select(id => new QueueEntry(id, false)));
        _items.Add(current);
        _items.AddRange(manual);
        _items.AddRange(after.Select(id => new QueueEntry(id, false)));
        CurrentIndex = before.Count;
        SourceAnchorIndex = here < 0 ? CurrentIndex : -1;
        Context = context;
        _sourceList = list.ToArray();
        Changed?.Invoke();
    }

    /// <summary>用户切换随机：打开时当前这首之外全部重新打乱；关闭时恢复列表顺序。</summary>
    public void SetShuffle(bool enabled)
    {
        if (enabled == Shuffle) return;
        Shuffle = enabled;
        if (_items.Count == 0) { Changed?.Invoke(); return; }
        // 没有来源时用队列里现有的歌（不含手动添加的）
        var list = _sourceList ?? _items.Where(e => !e.Manual).Select(e => e.TrackId).Distinct().ToList();
        if (enabled) Apply(list, history: [], reshuffle: true);
        else Apply(list, history: null);
        Changed?.Invoke();
    }

    /// <summary>
    /// 按列表 <paramref name="list"/> 重排：当前歌曲和它后面手动添加的保持不动，替换前后来自列表的部分。
    /// <paramref name="history"/> 不为 null 时按随机处理：它们（还在列表里的）放在前面，后面是其余的歌，
    /// 保持原来的随机顺序，新的插到随机位置（<paramref name="reshuffle"/> 时全部重新打乱）；
    /// 为 null 时按列表顺序，当前歌曲放在它在列表里的位置。
    /// </summary>
    void Apply(IReadOnlyList<long> list, List<long>? history, bool reshuffle = false)
    {
        var c = CurrentIndex;
        var current = _items[c];
        var sourceAnchor = SourceAnchorIndex == c && !list.Contains(current.TrackId);
        var inList = list.ToHashSet();
        var laterSource = _items.Skip(c + 1).Where(e => !e.Manual).Select(e => e.TrackId).ToList();
        List<long> before, after;
        if (history is not null)
        {
            before = history.Where(id => inList.Contains(id) && id != current.TrackId).Distinct().ToList();
            var played = before.ToHashSet();
            var kept = reshuffle ? new List<long>() : laterSource.Where(id => inList.Contains(id) && id != current.TrackId && !played.Contains(id)).Distinct().ToList();
            var keptSet = kept.ToHashSet();
            foreach (var id in list.Where(id => id != current.TrackId && !played.Contains(id) && !keptSet.Contains(id)).Distinct())
                kept.Insert(_random.Next(kept.Count + 1), id);
            after = kept;
        }
        else
        {
            var here = !current.Manual ? IndexOf(list, current.TrackId) : -1;
            if (here >= 0)
            {
                before = list.Take(here).ToList();
                after = list.Skip(here + 1).ToList();
            }
            else if (sourceAnchor)
            {
                before = [];
                after = [.. list];
            }
            else
            {
                // 当前歌曲不在列表里了（或是手动添加的）：从它后面第一首还在列表里的歌接着播
                var nextId = laterSource.Cast<long?>().FirstOrDefault(id => inList.Contains(id!.Value));
                var next = nextId is { } n ? IndexOf(list, n) : list.Count;
                before = list.Take(next).ToList();
                after = list.Skip(next).ToList();
            }
        }
        var manual = ManualAfterCurrent();
        var oldBefore = _items.Take(c).ToList();
        var oldAfter = _items.Skip(c + 1).ToList();
        if (oldBefore.Select(e => e.TrackId).SequenceEqual(before) && oldBefore.All(e => !e.Manual) &&
            oldAfter.Select(e => e.TrackId).SequenceEqual(manual.Select(e => e.TrackId).Concat(after)))
        {
            var anchor = sourceAnchor ? CurrentIndex : -1;
            if (SourceAnchorIndex != anchor) { SourceAnchorIndex = anchor; Changed?.Invoke(); }
            return;
        }

        _items.Clear();
        _items.AddRange(before.Select(id => new QueueEntry(id, false)));
        _items.Add(current);
        _items.AddRange(manual);
        _items.AddRange(after.Select(id => new QueueEntry(id, false)));
        CurrentIndex = before.Count;
        SourceAnchorIndex = sourceAnchor ? CurrentIndex : -1;
        Changed?.Invoke();
    }

    static int IndexOf(IReadOnlyList<long> list, long id)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    /// <summary>当前歌曲后面连着的手动添加的歌。</summary>
    List<QueueEntry> ManualAfterCurrent() =>
        _items.Count == 0 ? [] : _items.Skip(CurrentIndex + 1).TakeWhile(e => e.Manual).ToList();

    void ShuffleInPlace(List<long> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// 下一首的位置。<paramref name="user"/> 为 true 表示用户按了"下一首"：始终可用（需求 P-11），最后一首时回到第一首，
    /// 只有一首时还是它（从头重播）；单曲循环时用户也是切到下一首。为 false 表示自动播完：单曲循环时还是这首，
    /// 最后一首时列表循环回到第一首、否则停止（null）。
    /// 例外：新来源为空且只保留历史锚点时，没有合法前向目标，用户下一首也返回 null。
    /// </summary>
    public int? NextIndex(bool user)
    {
        if (_items.Count == 0) return null;
        if (!user && Repeat == RepeatMode.One) return CurrentIndex;
        for (var i = CurrentIndex + 1; i < _items.Count; i++) if (i != SourceAnchorIndex) return i;
        if (!user && Repeat != RepeatMode.All) return null;
        for (var i = 0; i < _items.Count; i++) if (i != SourceAnchorIndex) return i;
        return null;
    }

    /// <summary>上一首的位置：第一首时列表循环回到最后一首，否则还是第一首（从头播）。</summary>
    public int? PreviousIndex()
    {
        if (_items.Count == 0) return null;
        if (CurrentIndex > 0) return CurrentIndex - 1;
        if (Repeat != RepeatMode.All) return 0;
        for (var i = _items.Count - 1; i >= 0; i--) if (i != SourceAnchorIndex) return i;
        return 0;
    }

    /// <summary>切到 <paramref name="index"/>（自动换歌、用户切歌、在队列里点一首）。</summary>
    public void MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        CurrentIndex = index;
        Changed?.Invoke();
    }

    /// <summary>从队列里移除（队列界面的 ×）。移除当前这首时当前位置落到它后面那首（没有就是前一首）。</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        if (index == SourceAnchorIndex) SourceAnchorIndex = -1;
        else if (index < SourceAnchorIndex) SourceAnchorIndex--;
        if (_items.Count == 0) CurrentIndex = -1;
        else if (index < CurrentIndex) CurrentIndex--;
        else if (index == CurrentIndex) CurrentIndex = Math.Min(CurrentIndex, _items.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>在队列里拖动调整顺序。</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return;
        var entry = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, entry);
        if (SourceAnchorIndex == from) SourceAnchorIndex = to;
        else if (from < SourceAnchorIndex && to >= SourceAnchorIndex) SourceAnchorIndex--;
        else if (from > SourceAnchorIndex && to <= SourceAnchorIndex) SourceAnchorIndex++;
        if (CurrentIndex == from) CurrentIndex = to;
        else if (from < CurrentIndex && to >= CurrentIndex) CurrentIndex--;
        else if (from > CurrentIndex && to <= CurrentIndex) CurrentIndex++;
        Changed?.Invoke();
    }

    /// <summary>清空队列。</summary>
    public void Clear()
    {
        _items.Clear();
        CurrentIndex = -1;
        SourceAnchorIndex = -1;
        Context = null;
        _sourceList = null;
        Changed?.Invoke();
    }

    /// <summary>曲库里已经不存在的歌：从队列里去掉（扫描删除了文件、移除了音乐文件夹）。返回当前歌曲是否被去掉了。</summary>
    public bool RemoveMissing(Func<long, bool> exists)
    {
        if (_items.All(e => exists(e.TrackId))) return false;
        var currentGone = Current is { } cur && !exists(cur.TrackId);
        // 当前位置落到原来当前歌曲（或它后面第一首还在的歌）上；后面都没有了就是最后一首
        var kept = new List<QueueEntry>();
        var newCurrent = -1;
        var newAnchor = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (!exists(_items[i].TrackId)) continue;
            if (i >= CurrentIndex && newCurrent < 0) newCurrent = kept.Count;
            if (i == SourceAnchorIndex) newAnchor = kept.Count;
            kept.Add(_items[i]);
        }
        _items.Clear();
        _items.AddRange(kept);
        CurrentIndex = _items.Count == 0 ? -1 : newCurrent < 0 ? _items.Count - 1 : newCurrent;
        SourceAnchorIndex = newAnchor;
        Changed?.Invoke();
        return currentGone;
    }
}
