package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.MoodDao
import com.tinyfish.jeekmusic.data.settings.MoodFilter
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 全局的"运动 / 安静"筛选（需求 M-6）：标题栏上切换，所有歌曲列表都按它过滤，
 * 保存在设置里，切换标签和重启 App 后保持。
 */
@OptIn(ExperimentalCoroutinesApi::class)
@Singleton
class MoodFilterRepository @Inject constructor(
    private val settings: SettingsRepository,
    private val moodDao: MoodDao,
) {
    val filter: Flow<MoodFilter> = settings.settings.map { it.moodFilter }.distinctUntilChanged()

    /** 当前筛选允许的 track_key；null 表示不筛选。分类结果变化（分析完成、手动修正）时自动更新。 */
    val allowedKeys: Flow<Set<String>?> = filter.flatMapLatest { f ->
        val mood = f.mood ?: return@flatMapLatest flowOf(null)
        moodDao.observeKeys(mood).map { it.toHashSet() }
    }

    suspend fun set(filter: MoodFilter) = settings.setMoodFilter(filter)
}

/** 按 [allowedKeys] 过滤；null 表示不筛选，原样返回。 */
inline fun <T> List<T>.filterByMood(allowedKeys: Set<String>?, key: (T) -> String?): List<T> =
    if (allowedKeys == null) this else filter { item -> key(item)?.let { it in allowedKeys } == true }
