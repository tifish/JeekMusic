package com.tinyfish.jeekmusic.data.online

import android.content.Context
import android.util.Log
import com.tinyfish.jeekmusic.data.cover.CoverMissLog
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 播放时获取（需求 C-3、LY-8）：由播放服务在换歌后调用，不管播放页开没开。
 * 自动获取打开、而且现在的网络允许时，这首歌没有封面就为它的专辑查一次，没有歌词就查它的歌词，
 * 不用等后台的批量获取排到它。规则和批量获取一样（[OnlineFetcher]）。
 */
@Singleton
class AutoFetcher @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
    private val fetcher: OnlineFetcher,
    private val misses: CoverMissLog,
) {
    suspend fun fetchFor(track: TrackEntity) = withContext(Dispatchers.IO) {
        val s = settings.current()
        val kinds = buildSet {
            if (s.onlineCovers && networkAllows(context, s.coverFetchMode)) add(DownloadKind.COVERS)
            if (s.onlineLyrics && networkAllows(context, s.lyricsFetchMode)) add(DownloadKind.LYRICS)
        }
        if (kinds.isEmpty()) return@withContext
        val plan = fetcher.plan(listOf(track), kinds, explicit = false, quick = true) ?: return@withContext
        Log.i(TAG, "fetching for ${track.title}: cover=${plan.coverTargets.isNotEmpty()} lyrics=${plan.lyricsTargets.isNotEmpty()}")
        val outcome = fetcher.execute(plan, explicit = false)
        misses.save()
        Log.i(TAG, "${track.title}: $outcome")
    }

    private companion object {
        const val TAG = "AutoFetcher"
    }
}
