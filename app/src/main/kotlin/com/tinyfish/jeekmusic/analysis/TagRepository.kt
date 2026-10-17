package com.tinyfish.jeekmusic.analysis

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.TagDao
import com.tinyfish.jeekmusic.data.db.TagOverrideEntity
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import com.tinyfish.jeekmusic.data.db.TrackTagEntity
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 一首歌的智能标签：[tags] 是最终结果（手动修正优先），[auto] 是自动识别的结果，
 * [manual] 是被手动修正过的维度。
 */
data class TagInfo(
    val tags: Map<TagDimension, Tag?>,
    val auto: Map<TagDimension, Tag?>,
    val manual: Set<TagDimension>,
    val features: TrackFeaturesEntity?,
)

/**
 * 智能标签的数据（docs/design.md §6.1）：track_tag = 每个维度手动修正优先，否则用分类器的结果。
 * 1 万首规模下全部重算只需要几十毫秒，所以分析结果或设置变化后直接全部重算。
 */
@Singleton
class TagRepository @Inject constructor(
    private val db: JeekDatabase,
    private val dao: TagDao,
    private val settings: SettingsRepository,
) {
    private val mutex = Mutex()

    fun pendingCount(): Flow<Int> = dao.observePendingCount(ANALYZER_VERSION)

    suspend fun recomputeAll() = mutex.withLock {
        val sensitivity = settings.current().sensitivity
        val overrides = dao.overrides().groupBy { it.trackKey }.mapValues { (_, list) -> list.mapTo(HashSet()) { it.dimension } }
        val auto = dao.allFeatures().flatMap { f ->
            val manual = overrides[f.trackKey].orEmpty()
            TagClassifier.classify(f, sensitivity).mapNotNull { (dimension, tag) ->
                if (dimension.name in manual || tag == null) null else TrackTagEntity(f.trackKey, dimension.name, tag.name, SOURCE_AUTO)
            }
        }
        val manual = dao.overrides().mapNotNull { o -> o.value?.let { TrackTagEntity(o.trackKey, o.dimension, it, SOURCE_MANUAL) } }
        db.withTransaction {
            dao.clearTags()
            dao.insertTags(auto + manual)
        }
    }

    /** 手动修正（需求 M-4）：[tag] 为 null 表示"这个维度的哪个标签都不是"。 */
    suspend fun setManual(trackKey: String, dimension: TagDimension, tag: Tag?) {
        dao.setOverride(TagOverrideEntity(trackKey, dimension.name, tag?.name))
        recomputeAll()
    }

    /** 恢复自动识别。 */
    suspend fun clearManual(trackKey: String, dimension: TagDimension) {
        dao.clearOverride(trackKey, dimension.name)
        recomputeAll()
    }

    suspend fun info(trackKey: String): TagInfo {
        val features = dao.features(trackKey)
        val auto = features?.takeIf { !it.failed }?.let { TagClassifier.classify(it, settings.current().sensitivity) }.orEmpty()
        val overrides = dao.overrides(trackKey).associateBy { TagDimension.valueOf(it.dimension) }
        val tags = TagDimension.entries.associateWith { d ->
            if (d in overrides) Tag.of(d, overrides.getValue(d).value) else auto[d]
        }
        return TagInfo(tags, auto, overrides.keys, features)
    }

    companion object {
        /**
         * 分析算法的版本；提高后所有歌曲重新分析。
         * 2：下混改为功率守恒（÷√声道数），响度比 v1 高约 3dB。
         * 3：加入 YAMNet 模型，识别人声 / 纯音乐、古典 / 流行，运动 / 安静改用模型得分。
         */
        const val ANALYZER_VERSION = 3
        const val SOURCE_AUTO = "AUTO"
        const val SOURCE_MANUAL = "MANUAL"
    }
}
