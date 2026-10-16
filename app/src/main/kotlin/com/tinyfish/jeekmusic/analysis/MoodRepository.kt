package com.tinyfish.jeekmusic.analysis

import androidx.room.withTransaction
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.MoodDao
import com.tinyfish.jeekmusic.data.db.MoodOverrideEntity
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import com.tinyfish.jeekmusic.data.db.TrackMoodEntity
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject
import javax.inject.Singleton

data class MoodInfo(val mood: Mood?, val manual: Boolean, val features: TrackFeaturesEntity?)

/**
 * 智能标签的数据（docs/design.md §6.1）：track_mood = 手动修正优先，否则用分类器的结果。
 * 1 万首规模下全部重算只需要几十毫秒，所以特征或设置变化后直接全部重算。
 */
@Singleton
class MoodRepository @Inject constructor(
    private val db: JeekDatabase,
    private val dao: MoodDao,
    private val settings: SettingsRepository,
) {
    private val classifier: MoodClassifier = RuleBasedMoodClassifier
    private val mutex = Mutex()

    val analyzerVersion: Int get() = ANALYZER_VERSION

    fun tracks(mood: Mood): Flow<List<TrackEntity>> = dao.observeTracks(mood.name)

    fun pendingCount(): Flow<Int> = dao.observePendingCount(ANALYZER_VERSION)

    suspend fun recomputeAll() = mutex.withLock {
        val sensitivity = settings.current().sensitivity
        val overrides = dao.overrides().associateBy { it.trackKey }
        val auto = dao.allFeatures().mapNotNull { f ->
            if (f.trackKey in overrides) return@mapNotNull null
            classifier.classify(f.toFeatures(), sensitivity)?.let { TrackMoodEntity(f.trackKey, it.name, SOURCE_AUTO) }
        }
        val manual = overrides.values.mapNotNull { o -> o.mood?.let { TrackMoodEntity(o.trackKey, it, SOURCE_MANUAL) } }
        db.withTransaction {
            dao.clearMoods()
            dao.insertMoods(auto + manual)
        }
    }

    /** 手动修正（需求 M-4）：mood 为 null 表示"不属于任何标签"。 */
    suspend fun setManual(trackKey: String, mood: Mood?) {
        dao.setOverride(MoodOverrideEntity(trackKey, mood?.name))
        recomputeAll()
    }

    suspend fun clearManual(trackKey: String) {
        dao.clearOverride(trackKey)
        recomputeAll()
    }

    suspend fun info(trackKey: String): MoodInfo {
        val override = dao.override(trackKey)
        val mood = dao.mood(trackKey)?.mood?.let { runCatching { Mood.valueOf(it) }.getOrNull() }
        return MoodInfo(mood, override != null, dao.features(trackKey))
    }

    fun scores(features: TrackFeaturesEntity): Map<Mood, Double?> = classifier.scores(features.toFeatures())

    companion object {
        /**
         * 特征算法的版本；提高后所有歌曲重新分析。
         * 2：下混改为功率守恒（÷√声道数），响度比 v1 高约 3dB。
         */
        const val ANALYZER_VERSION = 2
        const val SOURCE_AUTO = "AUTO"
        const val SOURCE_MANUAL = "MANUAL"
    }
}

fun TrackFeaturesEntity.toFeatures() = AudioFeatures(bpm, bpmConfidence, loudness, loudnessRange, onsetRate, centroidHz, lowRatio)
