package com.tinyfish.jeekmusic.data.online

import com.tinyfish.jeekmusic.data.settings.OnlineSource

/** 在线音乐服务里搜到的一首歌，用来取封面和歌词。 */
data class OnlineSong(
    val source: OnlineSource,
    /** 各服务自己的歌曲 id（网易云是数字，QQ 音乐是 songmid）。 */
    val id: String,
    val title: String,
    val artist: String,
    val album: String?,
    val durationMs: Long?,
    /** 封面：缩略图和大图；没有封面时为 null。 */
    val thumbUrl: String?,
    val imageUrl: String?,
)
