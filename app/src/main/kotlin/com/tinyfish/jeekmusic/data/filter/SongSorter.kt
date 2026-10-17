package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.SortKeys
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.util.NaturalOrderComparator
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 全局的歌曲排序（需求 U-8）：标题栏菜单里选一次，所有歌曲列表都按它排；"默认"时各列表用自己的顺序
 * （文件夹按文件名、艺术家按歌名、专辑按音轨号、播放列表按手动顺序）。
 * 修改时间新的在前；文件名、歌名、歌手从 A 到 Z，中文按拼音，数字按数值（"2" 在 "10" 前面）。
 */
@Singleton
class SongSorter @Inject constructor(settings: SettingsRepository) {

    val sort: Flow<SongSort> = settings.settings.map { it.songSort }.distinctUntilChanged()

    private val sortKeys by lazy { SortKeys.android() }

    /** 拼音排序键的缓存：转写比较慢，长列表每次排序都重新算会卡。 */
    private val keyCache = ConcurrentHashMap<String, String>()

    /** 中文转拼音后的排序键（"周杰伦" → "zhou jie lun"）。 */
    fun key(text: String): String = keyCache.getOrPut(text) { sortKeys.sortKey(text) }

    private val byFileName: Comparator<TrackEntity> =
        compareBy<TrackEntity, String>(NaturalOrderComparator) { it.fileName }.thenBy { it.cueTrackNo ?: 0 }

    /** 按歌名（拼音），同名的按文件名。艺术家页的默认顺序也是这个。 */
    val byTitle: Comparator<TrackEntity> = compareBy<TrackEntity, String>(NaturalOrderComparator) { key(it.title) }.then(byFileName)

    /**
     * 按 [sort] 排序；[track] 为 null 的条目（播放列表里暂时不可用的歌）排在最后。
     * [SongSort.DEFAULT] 时返回 null，由调用方用列表自己的默认顺序。
     */
    fun <T> sorted(items: List<T>, sort: SongSort, track: (T) -> TrackEntity?): List<T>? {
        val comparator = comparator(sort) ?: return null
        val (present, missing) = items.partition { track(it) != null }
        return present.sortedWith(compareBy(comparator) { track(it)!! }) + missing
    }

    fun comparator(sort: SongSort): Comparator<TrackEntity>? = when (sort) {
        SongSort.DEFAULT -> null
        SongSort.MODIFIED -> compareByDescending<TrackEntity> { it.dateModified }.then(byFileName)
        SongSort.FILE_NAME -> byFileName
        SongSort.TITLE -> byTitle
        // "未知艺术家"排在最后
        SongSort.ARTIST -> compareBy<TrackEntity> { it.artistDisplay.isEmpty() }
            .thenBy(NaturalOrderComparator) { key(it.artistDisplay) }
            .then(byTitle)
    }
}
