package com.tinyfish.jeekmusic.playback

import android.net.Uri
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/**
 * 封面换过的歌曲（需求 C-3）。图片加载库按 URI 缓存，换了封面后给这些歌曲的封面 URI 加上版本号，界面才会重新加载。
 * 只在本次运行内有效：重启后内存缓存本来就是空的。
 */
object ArtworkVersions {
    private val _versions = MutableStateFlow<Map<Long, Int>>(emptyMap())
    val versions: StateFlow<Map<Long, Int>> = _versions

    fun bump(trackIds: Collection<Long>) = _versions.update { current ->
        current + trackIds.associateWith { (current[it] ?: 0) + 1 }
    }

    /** content://…/track/<id>/<size> 加上版本号；没换过封面时原样返回。 */
    fun withVersion(uri: Uri, versions: Map<Long, Int>): Uri {
        val trackId = uri.pathSegments.getOrNull(1)?.toLongOrNull() ?: return uri
        val version = versions[trackId] ?: return uri
        return uri.buildUpon().appendQueryParameter("v", version.toString()).build()
    }
}
