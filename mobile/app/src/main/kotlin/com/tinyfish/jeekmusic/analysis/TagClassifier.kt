package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import kotlin.math.abs
import kotlin.math.ln
import kotlin.math.log2

/** 智能标签的四个维度（需求 M-1）：每首歌在每个维度上最多一个标签。 */
enum class TagDimension { MOOD, VOICE, GENRE, CALMNESS }

enum class Tag(val dimension: TagDimension) {
    SPORT(TagDimension.MOOD),
    NON_SPORT(TagDimension.MOOD),
    VOCAL(TagDimension.VOICE),
    INSTRUMENTAL(TagDimension.VOICE),
    CLASSICAL(TagDimension.GENRE),
    NON_CLASSICAL(TagDimension.GENRE),
    CALM(TagDimension.CALMNESS),
    NON_CALM(TagDimension.CALMNESS),
    ;

    companion object {
        /** 旧版本曲风分"古典 / 流行"、氛围分"运动 / 安静"，保存的筛选和手动修正里可能还是 POP、QUIET。 */
        private const val LEGACY_POP = "POP"
        private const val LEGACY_QUIET = "QUIET"

        fun of(dimension: TagDimension, value: String?): Tag? = when {
            dimension == TagDimension.GENRE && value == LEGACY_POP -> NON_CLASSICAL
            dimension == TagDimension.MOOD && value == LEGACY_QUIET -> NON_SPORT
            else -> entries.firstOrNull { it.dimension == dimension && it.name == value }
        }
        fun of(dimension: TagDimension): List<Tag> = entries.filter { it.dimension == dimension }
    }
}

/**
 * 运动的识别灵敏度（需求 M-8）。阈值越低，归入"运动"的歌越多。
 * [legacy] 是旧的规则分类器（还没用模型重新分析的歌）使用的阈值。
 */
enum class Sensitivity(val sport: Double, val legacy: Double) {
    LOOSE(2.75, 0.50),
    NORMAL(3.25, 0.60),
    STRICT(3.75, 0.70),
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
    /** Orchestra 的得分：以前用来排除大编制管弦乐段落（不算"安静"），现在没有用到，留着供校准分析。 */
    val orchestra: Double,
)

/**
 * 根据模型得分分类（docs/design.md §6.4）。阈值用 109 首真实歌曲的人工标注校准
 * （tools/calibration/，人声 106/106、古典 / 非古典 97/103、运动 / 非运动 72/73），
 * 古典再用作者整个曲库（1551 首）按曲名粗标注检验：古典 / 非古典 95.2%。
 * 规则只依赖单首歌自己的得分，新增歌曲不会改变已有歌曲的标签。
 */
object TagClassifier {
    /** 人声和乐器之比的阈值：校准集里纯音乐最高 −2.83、人声最低 −1.72，取中间。 */
    const val VOCAL_THRESHOLD = -2.3
    /** 古典曲风类和其他曲风类之比的阈值：在两个标注集上扫过 −1.5～0.3，−0.4 的准确率都最高。 */
    const val CLASSICAL_THRESHOLD = -0.4

    /** 从这个分析版本起 classical_score 只用曲风类（[AudioTagger]）；更早的结果在重新分析前沿用旧阈值。 */
    private const val GENRE_ONLY_VERSION = 4

    /**
     * [classicalTitle]：歌名或专辑名里有古典作品的标志（[ClassicalTitles]），模型判为非古典时用它补救（需求 M-10）。
     */
    fun classify(f: TrackFeaturesEntity, sensitivity: Sensitivity, classicalTitle: Boolean = false): Map<TagDimension, Tag?> {
        val m = f.modelScores() ?: return buildMap {
            put(TagDimension.MOOD, LegacyMoodClassifier.classify(f.toFeatures(), sensitivity))
            // 还没用模型分析的歌：只凭曲名判断得出古典，判断不出非古典
            if (classicalTitle) put(TagDimension.GENRE, Tag.CLASSICAL)
        }
        val evidence = if (f.analyzerVersion == 5) AnalysisEvidence.parse(f.analysisEvidence) else null
        val a = evidence?.takeIf { it.complete }?.aggregate()
        val genre = if (a != null) { if (a.classical >= CLASSICAL_THRESHOLD) Tag.CLASSICAL else Tag.NON_CLASSICAL }
            else if (f.analyzerVersion >= GENRE_ONLY_VERSION) genre(m) else legacyGenre(m)
        return mapOf(
            TagDimension.MOOD to if (a == null) mood(m, sensitivity) else if (a.sport >= sensitivity.sport) Tag.SPORT else Tag.NON_SPORT,
            TagDimension.VOICE to if (a == null) voice(m) else if (a.vocal >= VOCAL_THRESHOLD) Tag.VOCAL else Tag.INSTRUMENTAL,
            TagDimension.GENRE to if (classicalTitle) Tag.CLASSICAL else genre,
            TagDimension.CALMNESS to if (f.failed) null else CalmnessEvidence.from(evidence)?.tag,
        )
    }

    /** 运动 / 非运动（需求 M-1）：和人声 / 纯音乐一样总有一个，慢歌、中速的歌、管弦乐等都算非运动。 */
    fun mood(m: ModelScores, sensitivity: Sensitivity): Tag = if (sportScore(m) >= sensitivity.sport) Tag.SPORT else Tag.NON_SPORT

    fun voice(m: ModelScores): Tag = if (m.vocal >= VOCAL_THRESHOLD) Tag.VOCAL else Tag.INSTRUMENTAL

    /** 古典 / 非古典（需求 M-1）：和人声 / 纯音乐一样总有一个，爵士、电影配乐等都算非古典。 */
    fun genre(m: ModelScores): Tag = if (m.classical >= CLASSICAL_THRESHOLD) Tag.CLASSICAL else Tag.NON_CLASSICAL

    /** v3 的 classical_score 把钢琴、弦乐等乐器也算作古典，阈值不同；只在重新分析完成前使用。 */
    private fun legacyGenre(m: ModelScores): Tag = if (m.classical >= 0.5) Tag.CLASSICAL else Tag.NON_CLASSICAL

    /** ln(动感 / 舒缓)。 */
    fun sportScore(m: ModelScores) = ln(m.energy + 0.003) - ln(m.calm + 0.003)
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
 * 保证升级后到重新分析完成之前"运动"不会突然变空。
 */
object LegacyMoodClassifier {

    fun classify(features: AudioFeatures, sensitivity: Sensitivity): Tag {
        val sport = sportScore(features)
        return if (sport != null && sport >= sensitivity.legacy) Tag.SPORT else Tag.NON_SPORT
    }

    private fun sportScore(f: AudioFeatures): Double? {
        val bpm = f.bpm ?: return null
        if (bpm !in 105.0..185.0 || f.bpmConfidence < 1.3 || f.loudness < -12) return null
        return 0.35 * norm(f.loudness, -12.0, -7.0) +
            0.30 * norm(f.lowRatio, 0.2, 0.6) +
            0.20 * (1 - abs(log2(bpm / 130.0))).coerceIn(0.0, 1.0) +
            0.15 * norm(f.onsetRate, 2.5, 4.5)
    }

    private fun norm(value: Double, low: Double, high: Double) = ((value - low) / (high - low)).coerceIn(0.0, 1.0)
}

fun TrackFeaturesEntity.complete() = !failed && modelScores() != null && AnalysisEvidence.parse(analysisEvidence)?.complete == true
fun TrackFeaturesEntity.usable() = !failed && analyzerVersion <= TagRepository.ANALYZER_VERSION &&
    (analyzerVersion < 5 || complete())
