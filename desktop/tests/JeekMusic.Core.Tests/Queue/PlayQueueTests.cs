using JeekMusic.Core.Queue;

namespace JeekMusic.Core.Tests.Queue;

/// <summary>队列规则（需求 P-6、P-11、P-15），对应移动端 QueueController 的行为。</summary>
public class PlayQueueTests
{
    static readonly QueueContext Folder = new(new QueueSource.Folder(1, "", false), "散曲");

    static PlayQueue Queue() => new(new Random(7));

    static long[] Ids(PlayQueue q) => q.Items.Select(e => e.TrackId).ToArray();

    [Fact]
    public void Play_in_order_starts_at_clicked_song()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4], startIndex: 2);
        Assert.Equal([1, 2, 3, 4], Ids(q));
        Assert.Equal(2, q.CurrentIndex);
        Assert.Equal(3, q.Current!.Value.TrackId);
    }

    [Fact]
    public void Play_shuffled_puts_clicked_song_first_and_keeps_all()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4, 5], startIndex: 3, shuffle: true);
        Assert.Equal(0, q.CurrentIndex);
        Assert.Equal(4, q.Items[0].TrackId);
        Assert.Equal([1, 2, 3, 4, 5], Ids(q).Order().ToArray());
        Assert.True(q.Shuffle);
    }

    [Fact]
    public void PlayNext_inserts_after_current_and_AddToQueue_after_manual()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3], startIndex: 0);
        q.AddToQueue([10]);
        q.PlayNext([20]);
        q.AddToQueue([30]);
        Assert.Equal([1, 20, 10, 30, 2, 3], Ids(q));
        Assert.All(q.Items.Skip(1).Take(3), e => Assert.True(e.Manual));
    }

    [Fact]
    public void Manual_songs_survive_playing_another_list()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3], startIndex: 0);
        q.PlayNext([20]);
        q.Play(Folder, [5, 6], startIndex: 0);
        Assert.Equal([5, 20, 6], Ids(q));
    }

    [Fact]
    public void Empty_queue_PlayNext_plays_without_source()
    {
        var q = Queue();
        q.PlayNext([7, 8]);
        Assert.Equal([7, 8], Ids(q));
        Assert.Equal(0, q.CurrentIndex);
        Assert.Null(q.Context);
    }

    [Fact]
    public void Regenerate_in_order_keeps_current_and_manual()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4], startIndex: 1);
        q.SourceChanged([1, 2, 3, 4]); // 第一次只记下来
        q.PlayNext([20]);
        q.SourceChanged([4, 2, 1]); // 排序变了、3 被筛掉
        Assert.Equal([4, 2, 20, 1], Ids(q));
        Assert.Equal(2, q.Current!.Value.TrackId);
    }

    [Fact]
    public void Regenerate_when_current_left_the_list_continues_from_next_remaining()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4], startIndex: 1);
        q.SourceChanged([1, 3, 4]);
        // 2 不在列表里了：保留它作为当前歌曲，从它后面第一首还在的（3）接着播
        Assert.Equal([1, 2, 3, 4], Ids(q));
        Assert.Equal(1, q.CurrentIndex);
    }

    [Fact]
    public void Regenerate_shuffled_keeps_played_and_order_inserts_new()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4, 5], startIndex: 0, shuffle: true);
        q.MoveTo(2);
        var played = Ids(q).Take(2).ToArray();
        var current = q.Current!.Value.TrackId;
        var upcoming = Ids(q).Skip(3).ToArray();
        q.SourceChanged([1, 2, 3, 4, 5, 6]);
        q.SourceChanged([1, 2, 3, 4, 5, 6, 7]);
        var ids = Ids(q);
        Assert.Equal(played, ids.Take(2));
        Assert.Equal(current, ids[2]);
        // 原来还没播的保持相对顺序，新加的 7 插在其中某处
        Assert.Equal(upcoming, ids.Skip(3).Where(id => upcoming.Contains(id)));
        Assert.Contains(7L, ids);
        Assert.Equal(7, ids.Length);
    }

    [Fact]
    public void Shuffle_off_restores_list_order_around_current()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4, 5], startIndex: 2, shuffle: true);
        q.SetShuffle(false);
        Assert.Equal([1, 2, 3, 4, 5], Ids(q));
        Assert.Equal(3, q.Current!.Value.TrackId);
        q.SetShuffle(true);
        Assert.Equal(3, q.Items[q.CurrentIndex].TrackId);
        Assert.Equal([1, 2, 3, 4, 5], Ids(q).Order().ToArray());
    }

    [Fact]
    public void Next_is_always_available_for_user()
    {
        var q = Queue();
        q.Play(Folder, [1, 2], startIndex: 1);
        Assert.Equal(0, q.NextIndex(user: true));   // 最后一首 → 第一首
        Assert.Null(q.NextIndex(user: false));      // 自动播完、不循环 → 停止
        q.Repeat = RepeatMode.All;
        Assert.Equal(0, q.NextIndex(user: false));
        q.Repeat = RepeatMode.One;
        Assert.Equal(1, q.NextIndex(user: false));  // 单曲循环自动播完还是这首
        Assert.Equal(0, q.NextIndex(user: true));   // 用户按下一首照样切歌

        var single = Queue();
        single.Play(Folder, [9], startIndex: 0);
        Assert.Equal(0, single.NextIndex(user: true)); // 只有一首：从头重播
    }

    [Fact]
    public void Previous_wraps_only_with_repeat_all()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3], startIndex: 0);
        Assert.Equal(0, q.PreviousIndex());
        q.Repeat = RepeatMode.All;
        Assert.Equal(2, q.PreviousIndex());
    }

    [Fact]
    public void Move_and_remove_keep_current_song()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4], startIndex: 1);
        q.Move(0, 3);
        Assert.Equal([2, 3, 4, 1], Ids(q));
        Assert.Equal(2, q.Current!.Value.TrackId);
        q.RemoveAt(0); // 移除当前这首 → 落到它后面那首
        Assert.Equal(3, q.Current!.Value.TrackId);
        q.RemoveAt(2);
        Assert.Equal([3, 4], Ids(q));
        Assert.Equal(3, q.Current!.Value.TrackId);
    }

    [Fact]
    public void RemoveMissing_moves_current_to_next_existing()
    {
        var q = Queue();
        q.Play(Folder, [1, 2, 3, 4], startIndex: 1);
        var gone = q.RemoveMissing(id => id is not (2 or 3));
        Assert.True(gone);
        Assert.Equal([1, 4], Ids(q));
        Assert.Equal(4, q.Current!.Value.TrackId);
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("playlist")]
    [InlineData("search")]
    public void QueueSource_roundtrip(string kind)
    {
        QueueSource source = kind switch
        {
            "folder" => new QueueSource.Folder(3, @"周杰伦\叶惠美\", true),
            "artist" => new QueueSource.Artist("周杰伦"),
            "album" => new QueueSource.Album("a:叶惠美|周杰伦"),
            "playlist" => new QueueSource.Playlist(12),
            _ => new QueueSource.Search("晴天"),
        };
        Assert.Equal(source, QueueSource.Decode(source.Encode()));
    }

    [Fact]
    public void QueueSource_decode_rejects_garbage()
    {
        Assert.Null(QueueSource.Decode(null));
        Assert.Null(QueueSource.Decode("folder\u001Fx"));
        Assert.Null(QueueSource.Decode("unknown"));
    }
}
