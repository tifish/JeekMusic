package com.tinyfish.jeekmusic.data.tabs

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.ArtistDao
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.PinnedTabEntity
import com.tinyfish.jeekmusic.data.db.TabDao
import com.tinyfish.jeekmusic.ui.navigation.Tab
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onStart
import javax.inject.Inject
import javax.inject.Singleton

/** 标签栏（docs/design.md §5.1）。 */
@Singleton
class TabRepository @Inject constructor(
    private val db: JeekDatabase,
    private val tabDao: TabDao,
    private val artistDao: ArtistDao,
) {
    val tabs: Flow<List<Tab>> = combine(
        tabDao.observeAll().onStart { ensureDefaults() },
        artistDao.observeAll(),
    ) { entities, artists ->
        val names = artists.associate { it.nameKey to it.name }
        entities.mapNotNull { it.toTab(names) }
    }

    val pinnedArtistKeys: Flow<Set<String>> = tabDao.observeAll().map { tabs ->
        tabs.filter { it.type == TYPE_ARTIST }.map { it.ref }.toSet()
    }

    /** 新固定的艺术家放在"文件夹"标签之后（不是追加到末尾），保证在标签栏里一眼能看到。 */
    suspend fun pinArtist(nameKey: String) {
        pinArtists(listOf(nameKey))
    }

    /**
     * 按给定顺序把一组艺术家固定到"文件夹"标签之后（需求 T-3）；已经固定的跳过。
     * 从备份恢复时也走这里，恢复的艺术家同样排在"文件夹"后面。返回新固定的个数。
     */
    suspend fun pinArtists(nameKeys: List<String>): Int = db.withTransaction {
        val tabs = tabDao.all()
        val pinned = tabs.filter { it.type == TYPE_ARTIST }.mapTo(HashSet()) { it.ref }
        val added = nameKeys.distinct().filter { it !in pinned }
        if (added.isEmpty()) return@withTransaction 0
        val at = tabs.indexOfFirst { it.type == TYPE_FOLDERS } + 1
        tabs.forEachIndexed { i, tab ->
            val position = if (i < at) i else i + added.size
            if (tab.position != position) tabDao.setPosition(tab.id, position)
        }
        tabDao.insert(added.mapIndexed { i, key -> PinnedTabEntity(type = TYPE_ARTIST, ref = key, position = at + i) })
        added.size
    }

    /** 内置标签不能取消固定（需求 T-2）。 */
    suspend fun unpin(tab: Tab) {
        when (tab) {
            is Tab.PinnedArtist -> tabDao.delete(TYPE_ARTIST, tab.nameKey)
            else -> Unit
        }
    }

    suspend fun move(from: Int, to: Int) = db.withTransaction {
        val tabs = tabDao.all().toMutableList()
        if (from !in tabs.indices || to !in tabs.indices) return@withTransaction
        tabs.add(to, tabs.removeAt(from))
        tabs.forEachIndexed { i, tab -> if (tab.position != i) tabDao.setPosition(tab.id, i) }
    }

    private suspend fun ensureDefaults() {
        // "运动 / 安静"从标签改成了全局筛选（需求 M-6），去掉旧版本固定的这两个标签
        tabDao.deleteType(TYPE_MOOD)
        if (tabDao.all().isNotEmpty()) return
        tabDao.insert(DEFAULTS.mapIndexed { i, (type, ref) -> PinnedTabEntity(type = type, ref = ref, position = i) })
    }

    private fun PinnedTabEntity.toTab(names: Map<String, String>): Tab? = when (type) {
        TYPE_FOLDERS -> Tab.Folders
        TYPE_ARTISTS -> Tab.Artists
        TYPE_PLAYLISTS -> Tab.Playlists
        TYPE_ARTIST -> Tab.PinnedArtist(ref, names[ref] ?: ref)
        else -> null
    }

    companion object {
        const val TYPE_FOLDERS = "FOLDERS"
        const val TYPE_ARTISTS = "ARTISTS"
        const val TYPE_PLAYLISTS = "PLAYLISTS"
        const val TYPE_ARTIST = "ARTIST"
        /** 旧版本的"运动 / 安静"标签，现在只用于清理。 */
        private const val TYPE_MOOD = "MOOD"

        /** 首次安装时的标签：文件夹、艺术家、播放列表（需求 T-2）。 */
        val DEFAULTS = listOf(
            TYPE_FOLDERS to "",
            TYPE_ARTISTS to "",
            TYPE_PLAYLISTS to "",
        )
    }
}
