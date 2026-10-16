package com.tinyfish.jeekmusic.startup

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.tinyfish.jeekmusic.data.settings.ThemeMode
import com.tinyfish.jeekmusic.ui.navigation.Tab
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 启动快照（docs/design.md §9.2）：首帧需要的少量数据，放在一个很小的 DataStore 里，
 * 不用等数据库打开、也不用等播放服务连接。
 */
data class Snapshot(
    val tabs: List<Tab> = emptyList(),
    val lastTabKey: String? = null,
    val themeMode: ThemeMode = ThemeMode.SYSTEM,
    val nowPlayingTrackId: Long = -1,
    val nowPlayingTitle: String? = null,
    val nowPlayingArtist: String? = null,
)

private val Context.snapshotStore: DataStore<Preferences> by preferencesDataStore(name = "startup")

@Singleton
class StartupSnapshot @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private object Keys {
        val TABS = stringPreferencesKey("tabs")
        val LAST_TAB = stringPreferencesKey("last_tab")
        val THEME = stringPreferencesKey("theme")
        val TRACK_ID = longPreferencesKey("now_playing_id")
        val TITLE = stringPreferencesKey("now_playing_title")
        val ARTIST = stringPreferencesKey("now_playing_artist")
    }

    @Volatile private var preloaded: CompletableDeferred<Snapshot>? = null

    /** 在 Application.onCreate 中调用：在后台线程预读，不阻塞主线程。 */
    fun preload() {
        val deferred = CompletableDeferred<Snapshot>().also { preloaded = it }
        Thread({
            deferred.complete(runCatching { runBlocking { load() } }.getOrDefault(Snapshot()))
        }, "snapshot-preload").start()
    }

    /** 预读过就等预读的结果，否则现读。 */
    suspend fun read(): Snapshot = preloaded?.await() ?: load()

    private suspend fun load(): Snapshot {
        val p = context.snapshotStore.data.first()
        return Snapshot(
            tabs = p[Keys.TABS]?.lines()?.mapNotNull(::decodeTab).orEmpty(),
            lastTabKey = p[Keys.LAST_TAB],
            themeMode = p[Keys.THEME]?.let { runCatching { ThemeMode.valueOf(it) }.getOrNull() } ?: ThemeMode.SYSTEM,
            nowPlayingTrackId = p[Keys.TRACK_ID] ?: -1,
            nowPlayingTitle = p[Keys.TITLE],
            nowPlayingArtist = p[Keys.ARTIST],
        )
    }

    suspend fun saveTabs(tabs: List<Tab>) = context.snapshotStore.edit { it[Keys.TABS] = tabs.joinToString("\n", transform = ::encodeTab) }

    suspend fun saveLastTab(key: String) = context.snapshotStore.edit { it[Keys.LAST_TAB] = key }

    suspend fun saveTheme(mode: ThemeMode) = context.snapshotStore.edit { it[Keys.THEME] = mode.name }

    suspend fun saveNowPlaying(trackId: Long, title: String?, artist: String?) = context.snapshotStore.edit {
        it[Keys.TRACK_ID] = trackId
        if (title == null) it.remove(Keys.TITLE) else it[Keys.TITLE] = title
        if (artist == null) it.remove(Keys.ARTIST) else it[Keys.ARTIST] = artist
    }

    private fun encodeTab(tab: Tab): String = when (tab) {
        is Tab.PinnedArtist -> "ARTIST\t${tab.nameKey}\t${tab.name}"
        is Tab.Mood -> "MOOD\t${tab.mood}"
        else -> tab.key
    }

    private fun decodeTab(line: String): Tab? {
        val parts = line.split('\t')
        return when (parts[0]) {
            Tab.Folders.key -> Tab.Folders
            Tab.Artists.key -> Tab.Artists
            Tab.Playlists.key -> Tab.Playlists
            "ARTIST" -> parts.getOrNull(1)?.let { Tab.PinnedArtist(it, parts.getOrNull(2) ?: it) }
            "MOOD" -> parts.getOrNull(1)?.let { Tab.Mood(it) }
            else -> null
        }
    }
}
