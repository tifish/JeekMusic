package com.tinyfish.jeekmusic.playback

import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ShuffleOrder
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.library.QueueSourceRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * 播放队列（需求 P-6、P-15，docs/design.md §7.6）。队列分两部分：
 * - **手动添加的**（"下一首播放""添加到队列"）：紧跟在当前歌曲后面，先播完它们；
 * - **来自列表的**：记住队列来自哪个列表（[QueueSource]），筛选、排序或曲库变了就按新的列表重新生成，
 *   当前这首不打断，手动添加的不受影响。
 *
 * 随机播放不用 ExoPlayer 的打乱顺序（它和列表一变就重新打乱、也分不出手动添加的），而是把队列本身排成随机的顺序，
 * ExoPlayer 用不打乱的 [ShuffleOrder.UnshuffledShuffleOrder]，"随机"只是个标记。这样队列里看到的就是实际的播放顺序，
 * 重新生成时还没播的歌保持原来的随机顺序，只去掉不符合的、把新加入的插到随机位置。
 */
@OptIn(UnstableApi::class)
class QueueController(
    private val player: ExoPlayer,
    private val scope: CoroutineScope,
    private val sources: QueueSourceRepository,
    private val onContextChanged: (QueueContext?) -> Unit,
) {
    /** 队列来自的列表和它的显示名称；没有时（只用"下一首播放"建立的队列、旧版本保存的队列）不重新生成。 */
    var context: QueueContext? = null
        private set

    /** 来自的列表里的歌（按列表顺序），最近一次从 [sources] 拿到的。 */
    private var sourceTracks: List<TrackEntity>? = null
    private var sourceJob: Job? = null

    /** 自己改随机标记时不按"用户切换了随机"处理。 */
    private var applyingShuffle = false

    init {
        player.setShuffleOrder(ShuffleOrder.UnshuffledShuffleOrder(player.mediaItemCount))
        player.addListener(object : Player.Listener {
            override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) {
                if (applyingShuffle) return
                // 不能在播放器的回调里直接改队列：媒体会话还拿着旧的当前位置，收到变短的队列会崩溃。放到回调结束后再做
                scope.launch(Dispatchers.Main) { if (player.shuffleModeEnabled == shuffleModeEnabled) reorder(shuffleModeEnabled) }
            }
        })
    }

    /** 断点续播时恢复来源；先不重新生成，等列表下次变化。 */
    fun restoreContext(value: QueueContext?) = setContext(value, initial = null)

    /** 断点续播时恢复随机标记；保存的队列已经是播放顺序，不重新排。 */
    fun restoreShuffle(enabled: Boolean) = setShuffle(enabled)

    /**
     * 播放 [tracks]（来自 [source] 的列表，按列表顺序），从 [startIndex] 开始；[startIndex] 为 null 时从头开始，随机时随机挑一首。
     * [shuffle] 为 null 时保持当前的随机模式。手动添加、还没播的歌保留在当前歌曲后面。
     */
    fun play(source: QueueSource, title: String, tracks: List<TrackEntity>, startIndex: Int?, shuffle: Boolean?) {
        if (tracks.isEmpty()) return
        val random = shuffle ?: player.shuffleModeEnabled
        val start = startIndex?.coerceIn(0, tracks.lastIndex) ?: if (random) tracks.indices.random() else 0
        val order = if (random) listOf(tracks[start]) + (tracks.take(start) + tracks.drop(start + 1)).shuffled() else tracks
        val current = if (random) 0 else start
        val manual = manualAfterCurrent()
        val items = order.map { MediaItemMapper.toMediaItem(it) }.toMutableList()
        items.addAll(current + 1, manual)
        setShuffle(random)
        player.setMediaItems(items, current, 0)
        player.prepare()
        player.play()
        setContext(QueueContext(source, title), initial = tracks)
    }

    /** "下一首播放"：插到当前歌曲后面。队列为空时直接播放这些歌。 */
    fun playNext(tracks: List<TrackEntity>) {
        if (tracks.isEmpty()) return
        if (player.mediaItemCount == 0) return playWithoutSource(tracks)
        player.addMediaItems(player.currentMediaItemIndex + 1, tracks.map { MediaItemMapper.toMediaItem(it, manual = true) })
    }

    /** "添加到队列"：加到手动添加的歌的最后，也就是来自列表的歌前面。 */
    fun addToQueue(tracks: List<TrackEntity>) {
        if (tracks.isEmpty()) return
        if (player.mediaItemCount == 0) return playWithoutSource(tracks)
        val index = player.currentMediaItemIndex + 1 + manualAfterCurrent().size
        player.addMediaItems(index, tracks.map { MediaItemMapper.toMediaItem(it, manual = true) })
    }

    private fun playWithoutSource(tracks: List<TrackEntity>) {
        player.setMediaItems(tracks.map { MediaItemMapper.toMediaItem(it) }, 0, 0)
        player.prepare()
        player.play()
        setContext(null, initial = null)
    }

    private fun setShuffle(enabled: Boolean) {
        applyingShuffle = true
        try {
            player.shuffleModeEnabled = enabled
        } finally {
            applyingShuffle = false
        }
    }

    private fun setContext(value: QueueContext?, initial: List<TrackEntity>?) {
        sourceJob?.cancel()
        context = value
        sourceTracks = initial
        onContextChanged(value)
        val source = value?.source ?: return
        sourceJob = scope.launch {
            sources.tracks(source).collect { tracks ->
                val previous = sourceTracks
                sourceTracks = tracks
                // 断点续播时第一次拿到的就是保存时的列表，只记下来
                if (previous != null && previous.map { it.id } != tracks.map { it.id }) regenerate(tracks)
            }
        }
    }

    /** 当前歌曲后面连着的手动添加的歌。 */
    private fun manualAfterCurrent(): List<MediaItem> {
        if (player.mediaItemCount == 0) return emptyList()
        val after = (player.currentMediaItemIndex + 1 until player.mediaItemCount).map { player.getMediaItemAt(it) }
        return after.takeWhile { MediaItemMapper.isManual(it) }
    }

    /** 用户切换了随机：重新排列来自列表的歌。打开时当前这首之外的随机排；关闭时恢复列表顺序。 */
    private fun reorder(shuffle: Boolean) {
        if (player.mediaItemCount == 0) return
        val items = items()
        val tracks = sourceTracks
        // 没有来源时用队列里现有的歌（不含手动添加的）
        val list = tracks?.map { it.id } ?: items.filterNot(MediaItemMapper::isManual).map(::idOf).distinct()
        val byId = tracks?.associateBy { it.id }
        if (shuffle) apply(items, list, byId, history = emptyList(), reshuffle = true) else apply(items, list, byId, history = null)
    }

    /** 来自的列表变了（筛选、排序、曲库）：按新列表重新生成。 */
    private fun regenerate(tracks: List<TrackEntity>) {
        if (player.mediaItemCount == 0) return
        val items = items()
        val c = player.currentMediaItemIndex
        // 随机时已经播过的歌（还在新列表里的）保留在前面，这一轮不再播
        val history = if (player.shuffleModeEnabled) items.take(c).filterNot(MediaItemMapper::isManual).map(::idOf) else null
        apply(items, tracks.map { it.id }, tracks.associateBy { it.id }, history)
    }

    /**
     * 按列表 [list]（歌曲 id，列表顺序）重排队列：当前歌曲和它后面手动添加的保持不动，替换前后来自列表的部分。
     * [history] 不为 null 时按随机处理：它们（还在列表里的）放在前面，后面是其余的歌，保持原来的随机顺序，新的插到随机位置
     * （[reshuffle] 时全部重新打乱）；为 null 时按列表顺序，当前歌曲放在它在列表里的位置。
     */
    private fun apply(items: List<MediaItem>, list: List<Long>, byId: Map<Long, TrackEntity>?, history: List<Long>?, reshuffle: Boolean = false) {
        val c = player.currentMediaItemIndex
        val current = items[c]
        val currentId = idOf(current)
        val inList = list.toSet()
        val laterSource = items.drop(c + 1).filterNot(MediaItemMapper::isManual).map(::idOf)
        val before: List<Long>
        val after: List<Long>
        if (history != null) {
            before = history.filter { it in inList && it != currentId }.distinct()
            val played = before.toSet()
            val kept = if (reshuffle) mutableListOf() else laterSource.filter { it in inList && it != currentId && it !in played }.distinct().toMutableList()
            val keptSet = kept.toSet()
            list.filter { it != currentId && it !in played && it !in keptSet }.distinct().forEach { kept.add((0..kept.size).random(), it) }
            after = kept
        } else {
            val here = if (!MediaItemMapper.isManual(current)) list.indexOf(currentId) else -1
            if (here >= 0) {
                before = list.take(here)
                after = list.drop(here + 1)
            } else {
                // 当前歌曲不在列表里了（或是手动添加的）：从它后面第一首还在列表里的歌接着播
                val next = laterSource.firstOrNull { it in inList }?.let(list::indexOf) ?: list.size
                before = list.take(next)
                after = list.drop(next)
            }
        }
        val manual = manualAfterCurrent()
        val oldBefore = items.take(c)
        val oldAfter = items.drop(c + 1)
        if (oldBefore.map(::idOf) == before && oldBefore.none(MediaItemMapper::isManual) &&
            oldAfter.map(::idOf) == manual.map(::idOf) + after
        ) return

        // 能复用的就用队列里现有的条目，其余从新列表的歌生成
        val existing = items.filterNot(MediaItemMapper::isManual).associateBy(::idOf)
        fun item(id: Long): MediaItem? = existing[id] ?: byId?.get(id)?.let { MediaItemMapper.toMediaItem(it) }
        player.replaceMediaItems(c + 1, items.size, manual + after.mapNotNull(::item))
        player.replaceMediaItems(0, c, before.mapNotNull(::item))
    }

    private fun items(): List<MediaItem> = List(player.mediaItemCount) { player.getMediaItemAt(it) }

    private fun idOf(item: MediaItem): Long = MediaItemMapper.trackId(item.mediaId) ?: -1
}

/** 队列来自的列表和显示名称（如文件夹名、艺术家名）。 */
data class QueueContext(val source: QueueSource, val title: String)
