package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.TagDao
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.TagFilter
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 全局的智能标签筛选（需求 M-6）：标题栏上切换，所有歌曲列表都按它过滤，
 * 保存在设置里，切换标签和重启 App 后保持。三个维度可以同时筛选（同时满足）。
 */
@OptIn(ExperimentalCoroutinesApi::class)
@Singleton
class TagFilterRepository @Inject constructor(
    private val settings: SettingsRepository,
    private val tagDao: TagDao,
) {
    val filter: Flow<TagFilter> = settings.settings.map { it.tagFilter }.distinctUntilChanged()

    /** 当前筛选允许的 track_key（各维度的交集）；null 表示不筛选。分类结果变化（分析完成、手动修正）时自动更新。 */
    val allowedKeys: Flow<Set<String>?> = filter.flatMapLatest { f ->
        val active = f.active
        if (active.isEmpty()) return@flatMapLatest flowOf(null)
        combine(active.map { tag -> tagDao.observeKeys(tag.dimension.name, tag.name) }) { lists ->
            lists.map { it.toHashSet() }.reduce { a, b -> a.apply { retainAll(b) } }
        }
    }

    suspend fun set(filter: TagFilter) = settings.setTagFilter(filter)
}

/** 按 [allowedKeys] 过滤；null 表示不筛选，原样返回。 */
inline fun <T> List<T>.filterByTags(allowedKeys: Set<String>?, key: (T) -> String?): List<T> =
    if (allowedKeys == null) this else filter { item -> key(item)?.let { it in allowedKeys } == true }
