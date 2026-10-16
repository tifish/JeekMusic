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

    suspend fun pinArtist(nameKey: String) {
        tabDao.insert(listOf(PinnedTabEntity(type = TYPE_ARTIST, ref = nameKey, position = tabDao.maxPosition() + 1)))
    }

    suspend fun pinMood(mood: String) {
        tabDao.insert(listOf(PinnedTabEntity(type = TYPE_MOOD, ref = mood, position = tabDao.maxPosition() + 1)))
    }

    /** 内置标签不能取消固定（需求 T-2）。 */
    suspend fun unpin(tab: Tab) {
        when (tab) {
            is Tab.PinnedArtist -> tabDao.delete(TYPE_ARTIST, tab.nameKey)
            is Tab.Mood -> tabDao.delete(TYPE_MOOD, tab.mood)
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
        if (tabDao.all().isNotEmpty()) return
        tabDao.insert(DEFAULTS.mapIndexed { i, (type, ref) -> PinnedTabEntity(type = type, ref = ref, position = i) })
    }

    private fun PinnedTabEntity.toTab(names: Map<String, String>): Tab? = when (type) {
        TYPE_FOLDERS -> Tab.Folders
        TYPE_ARTISTS -> Tab.Artists
        TYPE_PLAYLISTS -> Tab.Playlists
        TYPE_ARTIST -> Tab.PinnedArtist(ref, names[ref] ?: ref)
        TYPE_MOOD -> Tab.Mood(ref)
        else -> null
    }

    companion object {
        const val TYPE_FOLDERS = "FOLDERS"
        const val TYPE_ARTISTS = "ARTISTS"
        const val TYPE_PLAYLISTS = "PLAYLISTS"
        const val TYPE_ARTIST = "ARTIST"
        const val TYPE_MOOD = "MOOD"

        /** 首次安装时的标签：文件夹、艺术家、播放列表、运动、安静（需求 T-2、T-4）。 */
        val DEFAULTS = listOf(
            TYPE_FOLDERS to "",
            TYPE_ARTISTS to "",
            TYPE_PLAYLISTS to "",
            TYPE_MOOD to "SPORT",
            TYPE_MOOD to "QUIET",
        )
    }
}
