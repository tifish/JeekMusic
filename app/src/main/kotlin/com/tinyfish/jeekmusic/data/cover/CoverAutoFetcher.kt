package com.tinyfish.jeekmusic.data.cover

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.util.Log
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 播放时获取封面（需求 C-3）：自动获取打开时，开始播放一首没有封面的歌就立即为它的专辑查一次，
 * 不用等后台的批量获取排到它（批量获取按专辑逐张进行，曲库大时要很久）。
 * 规则和批量获取一样：同专辑已有封面就提取；没有艺术家的歌不查；30 天内没找到的不再查。
 */
@Singleton
class CoverAutoFetcher @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
    private val store: CoverStore,
    private val client: CoverClient,
    private val misses: CoverMissLog,
) {
    suspend fun fetchFor(track: TrackEntity) = withContext(Dispatchers.IO) {
        val s = settings.current()
        if (!s.onlineCovers || !networkAllows(s.coverFetchMode)) return@withContext
        if (store.hasCover(track)) return@withContext
        val key = track.albumKey ?: track.trackKey
        if (misses.recent(key)) return@withContext
        Log.i(TAG, "fetching cover for ${track.title}")
        val mode = store.effectiveMode(s.coverSaveMode)
        val album = store.albumOf(track)
        val query = album.firstOrNull { it.artistDisplay.isNotBlank() } ?: return@withContext
        val result = runCatching { client.fetchBest(query.titleTag ?: query.title, query.artistDisplay, query.album) }
            .onFailure { Log.w(TAG, "fetch failed: ${query.title}", it) }
        val image = result.getOrNull()
        when {
            image != null -> Log.i(TAG, "saved ${query.title}: ${store.save(listOf(query) + (album - query), image, mode)}")
            // 网络出错的不算"找不到"
            result.isSuccess -> {
                Log.i(TAG, "not found: ${query.title}")
                misses.add(key)
                misses.save()
            }
        }
    }

    private fun networkAllows(mode: FetchMode): Boolean {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return when (mode) {
            FetchMode.MANUAL -> false
            FetchMode.AUTO -> caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            FetchMode.WIFI -> caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
        }
    }

    private companion object {
        const val TAG = "CoverAutoFetcher"
    }
}
