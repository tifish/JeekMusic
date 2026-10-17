package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import kotlin.math.abs
import kotlin.math.ln
import kotlin.math.log2

/** 智能标签的三个维度（需求 M-1）：每首歌在每个维度上最多一个标签。 */
enum class TagDimension { MOOD, VOICE, GENRE }

enum class Tag(val dimension: TagDimension) {
    SPORT(TagDimension.MOOD),
    QUIET(TagDimension.MOOD),
    VOCAL(TagDimension.VOICE),
    INSTRUMENTAL(TagDimension.VOICE),
    CLASSICAL(TagDimension.GENRE),
    POP(TagDimension.GENRE),
    ;

    companion object {
        fun of(dimension: TagDimension, value: String?): Tag? = entries.firstOrNull { it.dimension == dimension && it.name == value }
        fun of(dimension: TagDimension): List<Tag> = entries.filter { it.dimension == dimension }
    }
}

/**
 * 运动 / 安静的识别灵敏度（需求 M-8）。阈值越低，归入标签的歌越多。
 * [legacy] 是旧的规则分类器（还没用模型重新分析的歌）使用的阈值。
 */
enum class Sensitivity(val sport: Double, val quiet: Double, val legacy: Double) {
    LOOSE(2.75, 0.0, 0.50),
    NORMAL(3.25, 0.2, 0.60),
    STRICT(3.75, 0.5, 0.70),
}

/** YAMNet 模型给出的几组得分（docs/design.md §6.3）。 */
data class ModelScores(
    /** ln(人声类 / 乐器类)，越大越像有人声。 */
    val vocal: Double,
    /** ln(古典曲风类 / 其他曲风类)，越大越像古典（v3 的分析结果是 ln(古典类和乐器类 / 流行类)，见 [TagClassifier.genre]）。 */
    val classical: Double,
    /** 舒缓类（Tender、Sad、Lullaby……）的得分之和。 */
    val calm: Double,
    /** 动感类（Exciting、Dance、Drum……）的得分之和。 */
    val energy: Double,
    /** Orchestra 的得分：大编制管弦乐段落通常不算"安静"。 */
    val orchestra: Double,
)

/**
 * 根据模型得分分类（docs/design.md §6.4）。阈值用 109 首真实歌曲的人工标注校准
 * （tools/calibration/，人声 106/106、古典 / 流行 92/98、运动 / 安静 64/73），
 * 古典再用作者整个曲库（1551 首）按曲名粗标注检验：古典 / 非古典 95%。
 * 规则只依赖单首歌自己的得分，新增歌曲不会改变已有歌曲的标签。
 */
object TagClassifier {
    /** 人声和乐器之比的阈值：校准集里纯音乐最高 −2.83、人声最低 −1.72，取中间。 */
    const val VOCAL_THRESHOLD = -2.3
    const val CLASSICAL_THRESHOLD = -0.4
    const val POP_THRESHOLD = -1.5
    const val ORCHESTRA_MAX = 0.1

    /** 从这个分析版本起 classical_score 只用曲风类（[AudioTagger]）；更早的结果在重新分析前沿用旧阈值。 */
    private const val GENRE_ONLY_VERSION = 4

    fun classify(f: TrackFeaturesEntity, sensitivity: Sensitivity): Map<TagDimension, Tag?> {
        val m = f.modelScores() ?: return mapOf(TagDimension.MOOD to LegacyMoodClassifier.classify(f.toFeatures(), sensitivity))
        return mapOf(
            TagDimension.MOOD to mood(m, sensitivity),
            TagDimension.VOICE to voice(m),
            TagDimension.GENRE to if (f.analyzerVersion >= GENRE_ONLY_VERSION) genre(m) else legacyGenre(m),
        )
    }

    fun mood(m: ModelScores, sensitivity: Sensitivity): Tag? = when {
        sportScore(m) >= sensitivity.sport -> Tag.SPORT
        quietScore(m) >= sensitivity.quiet && m.orchestra <= ORCHESTRA_MAX -> Tag.QUIET
        else -> null
    }

    fun voice(m: ModelScores): Tag = if (m.vocal >= VOCAL_THRESHOLD) Tag.VOCAL else Tag.INSTRUMENTAL

    /**
     * 介于两个阈值之间（爵士、电影配乐等）不属于古典也不属于流行。流行的阈值留得较低：
     * 把古典判成流行比判成"都不是"更难接受。
     */
    fun genre(m: ModelScores): Tag? = when {
        m.classical >= CLASSICAL_THRESHOLD -> Tag.CLASSICAL
        m.classical <= POP_THRESHOLD -> Tag.POP
        else -> null
    }

    /** v3 的 classical_score 把钢琴、弦乐等乐器也算作古典，阈值不同；只在重新分析完成前使用。 */
    private fun legacyGenre(m: ModelScores): Tag? = when {
        m.classical >= 0.5 -> Tag.CLASSICAL
        m.classical <= -0.5 -> Tag.POP
        else -> null
    }

    /** ln(动感 / 舒缓)。 */
    fun sportScore(m: ModelScores) = ln(m.energy + 0.003) - ln(m.calm + 0.003)

    /** ln(舒缓 / 动感)。 */
    fun quietScore(m: ModelScores) = ln(m.calm + 0.01) - ln(m.energy + 0.01)
}

fun TrackFeaturesEntity.modelScores(): ModelScores? {
    return ModelScores(
        vocal = vocalScore ?: return null,
        classical = classicalScore ?: return null,
        calm = calmLevel ?: return null,
        energy = energyLevel ?: return null,
        orchestra = orchestraLevel ?: return null,
    )
}

fun TrackFeaturesEntity.toFeatures() = AudioFeatures(bpm, bpmConfidence, loudness, loudnessRange, onsetRate, centroidHz, lowRatio)

/**
 * 旧的规则分类器（v2，只看响度、低频、节奏等手工特征）：只用于还没用模型重新分析过的歌，
 * 保证升级后到重新分析完成之前"运动 / 安静"不会突然变空。
 */
object LegacyMoodClassifier {

    fun classify(features: AudioFeatures, sensitivity: Sensitivity): Tag? {
        val sport = sportScore(features)
        val quiet = quietScore(features)
        val sportOk = sport != null && sport >= sensitivity.legacy
        val quietOk = quiet != null && quiet >= sensitivity.legacy
        return when {
            sportOk && quietOk -> if (sport!! >= quiet!!) Tag.SPORT else Tag.QUIET
            sportOk -> Tag.SPORT
            quietOk -> Tag.QUIET
            else -> null
        }
    }

    private fun sportScore(f: AudioFeatures): Double? {
        val bpm = f.bpm ?: return null
        if (bpm !in 105.0..185.0 || f.bpmConfidence < 1.3 || f.loudness < -12) return null
        return 0.35 * norm(f.loudness, -12.0, -7.0) +
            0.30 * norm(f.lowRatio, 0.2, 0.6) +
            0.20 * (1 - abs(log2(bpm / 130.0))).coerceIn(0.0, 1.0) +
            0.15 * norm(f.onsetRate, 2.5, 4.5)
    }

    private fun quietScore(f: AudioFeatures): Double? {
        if (f.loudness > -14) return null
        return 0.50 * norm(-f.loudness, 14.0, 26.0) +
            0.25 * norm(-f.lowRatio, -0.35, -0.05) +
            0.25 * norm(-f.centroidHz, -1600.0, -700.0)
    }

    private fun norm(value: Double, low: Double, high: Double) = ((value - low) / (high - low)).coerceIn(0.0, 1.0)
}
