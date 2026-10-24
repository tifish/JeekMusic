package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.SortKeys
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.util.FileModified
import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.util.NaturalOrderComparator
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 全局的歌曲排序（需求 U-8）：标题栏菜单里选一次，所有歌曲列表都按它排，默认按修改时间倒序
 * （播放列表按文件名时保持手动排的顺序，由调用方处理）。
 * 修改时间新的在前；文件名、歌名、歌手从 A 到 Z，中文按拼音，数字按数值（"2" 在 "10" 前面）。
 */
@Singleton
class SongSorter internal constructor(settings: Flow<Settings>, createSortKeys: () -> SortKeys) {
    @Inject constructor(settings: SettingsRepository) : this(settings.settings, { SortKeys.android() })

    val sort: Flow<SongSort> = settings.map { it.songSort }.distinctUntilChanged()
    /** No saved preference: keep playlist manual order and album track order. */
    val orderedViewSort: Flow<SongSort> = settings.map {
        if (it.hasSongSortPreference) it.songSort else SongSort.FILE_NAME
    }.distinctUntilChanged()
    val albumSort: Flow<SongSort?> = settings.map {
        it.songSort.takeIf { _ -> it.hasSongSortPreference }
    }.distinctUntilChanged()

    private val sortKeys by lazy(createSortKeys)

    /** 拼音排序键的缓存：转写比较慢，长列表每次排序都重新算会卡。 */
    private val keyCache = ConcurrentHashMap<String, String>()

    /** 中文转拼音后的排序键（"周杰伦" → "zhou jie lun"）。 */
    fun key(text: String): String = keyCache.getOrPut(text) { sortKeys.sortKey(text) }

    /** 按文件名（中文按拼音），CUE 整轨里的各首按音轨号。 */
    val byFileName: Comparator<TrackEntity> =
        compareBy<TrackEntity, String>(NaturalOrderComparator) { key(it.fileName) }
            .thenBy(NaturalOrderComparator) { it.fileName }
            .thenBy { it.fileKey }.thenBy { it.cueTrackNo ?: 0 }.thenBy { it.id }

    /** 按歌名（拼音），同名的按文件名。 */
    val byTitle: Comparator<TrackEntity> = compareBy<TrackEntity, String>(NaturalOrderComparator) { key(it.title) }.then(byFileName)

    /** 按 [sort] 排序；[track] 为 null 的条目（播放列表里暂时不可用的歌）排在最后。 */
    fun <T> sorted(items: List<T>, sort: SongSort, track: (T) -> TrackEntity?): List<T> {
        val comparator = comparator(sort)
        val (present, missing) = items.partition { track(it) != null }
        return present.sortedWith(compareBy(comparator) { track(it)!! }) + missing
    }

    fun albumSorted(tracks: List<TrackEntity>, sort: SongSort?): List<TrackEntity> =
        if (sort == null) tracks.sortedWith(compareBy<TrackEntity> { it.discNo ?: 1 }
            .thenBy { it.trackNo ?: it.cueTrackNo ?: Int.MAX_VALUE }.then(byFileName))
        else sorted(tracks, sort) { it }

    fun comparator(sort: SongSort): Comparator<TrackEntity> = when (sort) {
        SongSort.MODIFIED -> compareBy<TrackEntity> { !FileModified.isValid(it.dateModified) }
            .thenByDescending { if (FileModified.isValid(it.dateModified)) it.dateModified else 0 }
            .then(byFileName)
        SongSort.FILE_NAME -> byFileName
        SongSort.TITLE -> byTitle
        // "未知艺术家"排在最后
        SongSort.ARTIST -> compareBy<TrackEntity> { it.artistDisplay.isEmpty() }
            .thenBy(NaturalOrderComparator) { key(it.artistDisplay) }
            .then(byTitle)
    }
}
