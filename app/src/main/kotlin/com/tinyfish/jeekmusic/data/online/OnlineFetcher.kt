package com.tinyfish.jeekmusic.data.online

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.ParcelFileDescriptor
import android.util.Log
import com.tinyfish.jeekmusic.data.cover.CoverClient
import com.tinyfish.jeekmusic.data.cover.CoverMissLog
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.lyrics.LyricsRepository
import com.tinyfish.jeekmusic.data.settings.CoverSaveMode
import com.tinyfish.jeekmusic.data.settings.FetchMode
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 在线获取封面和歌词的共同流程（需求 C-3、LY-8，docs/design.md §7.4）：播放时获取、批量获取、列表下载都用它。
 * 按"一组歌"处理：同时要封面时一组是一张专辑（封面按专辑共用），只要歌词时一组是一首歌。
 *
 * 先用 [plan] 看这组歌缺什么（不联网），再用 [execute] 获取并保存。封面和歌词都要写入同一个音乐文件时，
 * 两者都下载完后在同一次打开文件里写入，只改一次文件。
 */
@Singleton
class OnlineFetcher @Inject constructor(
    private val settings: SettingsRepository,
    private val store: CoverStore,
    private val client: CoverClient,
    private val lyrics: LyricsRepository,
    private val misses: CoverMissLog,
) {
    /**
     * 一组歌要做的事。[key] 是"没找到封面"记录用的键（专辑，没有专辑时是歌曲）；
     * [coverTargets] 是还需要封面的文件（同专辑的都算上），[lyricsTargets] 是还没有歌词的歌。
     */
    data class Plan(val key: String, val coverTargets: List<TrackEntity>, val lyricsTargets: List<TrackEntity>)

    data class Outcome(val coverSaved: Boolean, val coverExtracted: Boolean, val lyricsSaved: Int)

    /**
     * [explicit]：用户明确要求的（列表下载），不跳过最近没找到的。
     * [quick]：播放时用，专辑已经有封面就不再看每个文件有没有自己的（那要逐个读文件），留给批量获取。
     * 没有要做的时返回 null。
     */
    suspend fun plan(group: List<TrackEntity>, kinds: Set<DownloadKind>, explicit: Boolean, quick: Boolean): Plan? = withContext(Dispatchers.IO) {
        val first = group.firstOrNull() ?: return@withContext null
        val coverTargets = if (DownloadKind.COVERS in kinds) {
            val mode = store.effectiveMode(settings.current().coverSaveMode)
            val album = store.albumOf(first)
            when {
                quick || mode == CoverSaveMode.HIDDEN_FOLDER -> if (store.hasCover(first)) emptyList() else album
                // 写入文件 / 同名图片：同专辑已有封面时也要给还没有的文件补上（从已有的提取）
                else -> album.filter { !store.hasOwnCover(it, mode) }
            }
        } else {
            emptyList()
        }
        val lyricsTargets = if (DownloadKind.LYRICS in kinds) {
            // 没有艺术家的歌只凭歌名很容易配错，不查。先看最近没找到的（只查数据库），hasLyrics 可能要读文件
            group.filter { it.artistDisplay.isNotBlank() && (explicit || !lyrics.recentlyNotFound(it)) && !lyrics.hasLyrics(it) }
        } else {
            emptyList()
        }
        Plan(first.albumKey ?: first.trackKey, coverTargets, lyricsTargets).takeIf { coverTargets.isNotEmpty() || lyricsTargets.isNotEmpty() }
    }

    /** 获取并保存；"没找到封面"的记录由调用方 [CoverMissLog.save]。 */
    suspend fun execute(plan: Plan, explicit: Boolean): Outcome = withContext(Dispatchers.IO) {
        val coverMode = store.effectiveMode(settings.current().coverSaveMode)

        // 封面：同专辑已有就提取，否则在线查一次
        var coverTargets = plan.coverTargets
        var image: ByteArray? = null
        var extracted = false
        if (coverTargets.isNotEmpty()) {
            image = store.albumImage(coverTargets.first())
            extracted = image != null
            if (image == null && (explicit || !misses.recent(plan.key))) {
                val query = coverTargets.firstOrNull { it.artistDisplay.isNotBlank() }
                if (query == null) {
                    misses.add(plan.key)
                } else {
                    val result = runCatching { client.fetchBest(query.titleTag ?: query.title, query.artistDisplay, query.album) }
                        .onFailure { if (it is CancellationException) throw it else Log.w(TAG, "cover fetch failed: ${query.title}", it) }
                    image = result.getOrNull()
                    // 网络出错的不算"找不到"，下次还会再试
                    if (image == null && result.isSuccess) misses.add(plan.key)
                    // 第一首决定隐藏目录里的文件名
                    coverTargets = listOf(query) + (coverTargets - query)
                }
            }
        }

        // 歌词：逐首查
        val texts = LinkedHashMap<TrackEntity, String>()
        for (t in plan.lyricsTargets) {
            runCatching { lyrics.fetchOnline(t) }
                .onFailure { if (it is CancellationException) throw it else Log.w(TAG, "lyrics fetch failed: ${t.title}", it) }
                .getOrNull()?.let { texts[t] = it }
        }

        // 都要写入同一个文件的，和封面一起写
        val also = HashMap<String, (ParcelFileDescriptor) -> Boolean>()
        if (image != null && coverMode == CoverSaveMode.EMBED) {
            val coverFiles = coverTargets.mapTo(HashSet()) { it.fileKey }
            for ((t, text) in texts) if (t.fileKey in coverFiles && lyrics.embedsInFile(t)) also[t.fileKey] = lyrics.embedOperation(text)
        }
        val saved = image?.let { store.save(coverTargets, it, coverMode, also) }
        val alsoDone = saved?.alsoDone.orEmpty()
        for ((t, text) in texts) if (t.fileKey in alsoDone) lyrics.onEmbedded(t) else lyrics.save(t, text)
        if (alsoDone.isNotEmpty()) Log.i(TAG, "wrote cover and lyrics together into ${alsoDone.size} file(s)")
        Outcome(coverSaved = saved != null, coverExtracted = extracted && saved != null, lyricsSaved = texts.size)
    }

    private companion object {
        const val TAG = "OnlineFetcher"
    }
}

/** 按获取方式，现在的网络能不能自动获取：仅 Wi-Fi 时要求不计流量的网络。 */
fun networkAllows(context: Context, mode: FetchMode): Boolean {
    if (mode == FetchMode.MANUAL) return false
    val cm = context.getSystemService(ConnectivityManager::class.java)
    val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
    return when (mode) {
        FetchMode.MANUAL -> false
        FetchMode.AUTO -> caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        FetchMode.WIFI -> caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }
}
