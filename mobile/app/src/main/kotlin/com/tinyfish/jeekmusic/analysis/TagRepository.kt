package com.tinyfish.jeekmusic.analysis

import android.util.Log
import org.json.JSONObject
import com.tinyfish.jeekmusic.data.db.TagReviewEntity
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

    suspend fun recomputeAll() = mutex.withLock { db.withTransaction { recomputeCore() } }

    private suspend fun recomputeCore() {
        val sensitivity = settings.current().sensitivity
        val overrides = dao.overrides().groupBy { it.trackKey }.mapValues { (_, list) -> list.mapTo(HashSet()) { it.dimension } }
        val classicalTitles = dao.titles().filter { ClassicalTitles.matches(it.title, it.album) }.mapTo(HashSet()) { it.trackKey }
        val auto = dao.allFeatures().filter { it.usable() }.flatMap { f ->
            val manual = overrides[f.trackKey].orEmpty()
            TagClassifier.classify(f, sensitivity, f.trackKey in classicalTitles).mapNotNull { (dimension, tag) ->
                if (dimension.name in manual || tag == null) null else TrackTagEntity(f.trackKey, dimension.name, tag.name, SOURCE_AUTO)
            }
        }
        val manual = dao.overrides().mapNotNull { o ->
            val dimension = TagDimension.entries.firstOrNull { it.name == o.dimension } ?: return@mapNotNull null
            overrideTag(dimension, o.value)?.let { TrackTagEntity(o.trackKey, o.dimension, it.name, SOURCE_MANUAL) }
        }
        db.withTransaction {
            dao.clearTags()
            val covered = (auto + manual).filter { it.dimension == TagDimension.GENRE.name }.map { it.trackKey }.toSet()
            val titleOnly = classicalTitles.filter { it !in covered && TagDimension.GENRE.name !in overrides[it].orEmpty() }
                .map { TrackTagEntity(it, TagDimension.GENRE.name, Tag.CLASSICAL.name, SOURCE_AUTO) }
            dao.insertTags(auto + manual + titleOnly)
        }
    }

    /**
     * 曲风规则改过之后（只有古典 / 非古典，原来没有曲风的歌现在是非古典）需要重算一次：App 启动时检查。
     */
    suspend fun recomputeIfRulesChanged() {
        val saved = settings.current().tagRulesVersion
        if (saved >= RULES_VERSION) return
        recomputeAll()
        settings.setTagRulesVersion(RULES_VERSION)
        Log.i(TAG, "tag rules $saved -> $RULES_VERSION: recomputed ${dao.countTags()} tags")
    }

    /**
     * 手动修正的值。旧版本的曲风有"流行"（POP）和"都不是"（null），现在都算非古典。
     */
    private fun overrideTag(dimension: TagDimension, value: String?): Tag? =
        Tag.of(dimension, value) ?: when (dimension) {
            TagDimension.GENRE -> Tag.NON_CLASSICAL
            TagDimension.MOOD -> Tag.NON_SPORT
            TagDimension.VOICE, TagDimension.CALMNESS -> null
        }

    /** 手动修正（需求 M-4）：[tag] 为 null 表示"这个维度的哪个标签都不是"（只有氛围可以）。 */
    suspend fun setManual(trackKey: String, dimension: TagDimension, tag: Tag?) = mutex.withLock {
        require(tag == null || tag.dimension == dimension) { "Invalid tag dimension" }
        require(dimension != TagDimension.CALMNESS || tag != null) { "Use clearManual to restore automatic calmness" }
        db.withTransaction {
            val before = reviewSnapshot(trackKey)
            dao.setOverride(TagOverrideEntity(trackKey, dimension.name, tag?.name))
            recomputeCore()
            dao.saveReview(TagReviewEntity(trackKey = trackKey, record = JSONObject()
                .put("recordedAt", System.currentTimeMillis()).put("provenance", "manual_correction_not_independent_truth")
                .put("before", before).put("after", reviewSnapshot(trackKey)).toString()))
        }
    }

    /** 恢复自动识别。 */
    suspend fun clearManual(trackKey: String, dimension: TagDimension) = mutex.withLock {
        db.withTransaction {
            val before = reviewSnapshot(trackKey)
            dao.clearOverride(trackKey, dimension.name)
            recomputeCore()
            dao.saveReview(TagReviewEntity(trackKey = trackKey, record = JSONObject()
                .put("recordedAt", System.currentTimeMillis()).put("provenance", "restore_automatic")
                .put("before", before).put("after", reviewSnapshot(trackKey)).toString()))
        }
    }

    private suspend fun reviewSnapshot(key: String): JSONObject {
        val info = info(key)
        fun values(map: Map<TagDimension, Tag?>) = JSONObject().also { o -> map.forEach { (d, v) -> o.put(d.name, v?.name ?: JSONObject.NULL) } }
        return JSONObject().put("automatic", values(info.auto)).put("final", values(info.tags))
            .put("manual", org.json.JSONArray(info.manual.map { it.name })).put("analyzerVersion", info.features?.analyzerVersion)
            .put("rulesVersion", RULES_VERSION).put("evidence", AnalysisEvidence.parse(info.features?.analysisEvidence)?.toJson()?.let(::JSONObject))
            .put("classicalTitleMatched", dao.title(key)?.let { ClassicalTitles.matches(it.title, it.album) } == true)
    }

    suspend fun info(trackKey: String): TagInfo {
        val features = dao.currentFeatures(trackKey)
        val classicalTitle = dao.title(trackKey)?.let { ClassicalTitles.matches(it.title, it.album) } == true
        val auto = features?.takeIf { it.usable() }?.let { TagClassifier.classify(it, settings.current().sensitivity, classicalTitle) }.orEmpty().toMutableMap().also { if (classicalTitle) it[TagDimension.GENRE] = Tag.CLASSICAL }
        val overrides = dao.overrides(trackKey).mapNotNull { o -> TagDimension.entries.firstOrNull { it.name == o.dimension }?.let { it to o } }.toMap()
        val tags = TagDimension.entries.associateWith { d ->
            if (d in overrides) overrideTag(d, overrides.getValue(d).value) else auto[d]
        }
        return TagInfo(tags, auto, overrides.keys, features)
    }

    companion object {
        /**
         * 分析算法的版本；提高后所有歌曲重新分析。
         * 2：下混改为功率守恒（÷√声道数），响度比 v1 高约 3dB。
         * 3：加入 YAMNet 模型，识别人声 / 纯音乐、古典 / 流行，运动 / 安静改用模型得分。
         * 4：classical_score 只比较曲风类（Classical music、Opera 对其他曲风），不再把钢琴、弦乐等乐器算作古典。
         */
        const val ANALYZER_VERSION = 5
        /** 分类规则（不需要重新分析的部分）的版本；1：曲风改为古典 / 非古典；2：用曲名补救漏判的古典；4：氛围改为运动 / 非运动（3 被测试版本用过，跳过）。 */
        const val RULES_VERSION = 6
        const val SOURCE_AUTO = "AUTO"
        private const val TAG = "TagRepository"
        const val SOURCE_MANUAL = "MANUAL"
    }
}
