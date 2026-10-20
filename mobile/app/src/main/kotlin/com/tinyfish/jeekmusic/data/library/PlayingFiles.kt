package com.tinyfish.jeekmusic.data.library

import com.tinyfish.jeekmusic.data.db.TrackEntity
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 播放器正在用的音乐文件：当前这首，和为无缝播放预先加载的下一首（docs/design.md §7.4，需求 P-14）。
 *
 * 这些文件不改写：在手机上，正在播放的歌被写入封面、歌词后，会提前二三十秒换到下一首。
 * [AudioFileEditor] 遇到它们时不写，由调用方先存成下载的文件（立即能显示）；播放器不再用这个文件后，
 * 播放服务把推迟的歌交给 [EmbedDownloadedScheduler.scheduleFor] 写进文件。
 */
@Singleton
class PlayingFiles @Inject constructor() {
    @Volatile private var busy: Set<String> = emptySet()

    /** 推迟写入的文件：file_key → 歌曲 id。 */
    private val deferred = ConcurrentHashMap<String, Long>()

    fun isBusy(fileKey: String) = fileKey in busy

    fun defer(track: TrackEntity) {
        deferred[track.fileKey] = track.id
    }

    /** 这首歌的文件在等播放器用完再写入（用于提示"播放完后写入"）。 */
    fun isDeferred(fileKey: String) = deferred.containsKey(fileKey)

    /** 播放服务在换歌、队列变化、开始 / 停止播放时更新；返回不再使用、有推迟写入的歌曲 id。 */
    fun update(fileKeys: Set<String>): List<Long> {
        busy = fileKeys
        val released = deferred.keys.filter { it !in fileKeys }
        return released.mapNotNull { deferred.remove(it) }
    }
}
