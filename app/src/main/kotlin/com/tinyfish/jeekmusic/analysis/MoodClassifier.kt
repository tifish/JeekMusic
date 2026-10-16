package com.tinyfish.jeekmusic.analysis

import kotlin.math.abs
import kotlin.math.log2

enum class Mood { SPORT, QUIET }

/** 识别灵敏度（需求 M-8）。阈值越低，归入标签的歌越多。 */
enum class Sensitivity(val threshold: Double) { LOOSE(0.50), NORMAL(0.60), STRICT(0.70) }

/**
 * 分类器接口（docs/design.md §6.4）。以后换成 AI 模型时实现同一个接口即可。
 * 特征和分类是分开的：调灵敏度或升级规则只需用已有特征重新分类，不用重新解码音频。
 */
interface MoodClassifier {
    val version: Int
    fun classify(features: AudioFeatures, sensitivity: Sensitivity): Mood?
    fun scores(features: AudioFeatures): Map<Mood, Double?>
}

/**
 * 基于规则的分类。规则只依赖绝对量，不依赖整个曲库的分布，新增歌曲不会改变已有歌曲的标签。
 *
 * v2 用真实曲库（109 首，流行 / 古典 / 民谣混合）校准（docs/design.md §6.4）：
 * - 起音密度在流行和古典之间几乎没有区分度（都在 3–4.8 次/秒），只给很小的权重
 * - 低频占比（鼓和贝斯）区分度最好：古典 0–0.2，流行 0.2–0.7
 * - 响度：现代流行 −7 ~ −12 LUFS，古典 −17 ~ −33 LUFS
 */
object RuleBasedMoodClassifier : MoodClassifier {

    override val version = 2

    override fun classify(features: AudioFeatures, sensitivity: Sensitivity): Mood? {
        val sport = sportScore(features)
        val quiet = quietScore(features)
        val sportOk = sport != null && sport >= sensitivity.threshold
        val quietOk = quiet != null && quiet >= sensitivity.threshold
        return when {
            sportOk && quietOk -> if (sport!! >= quiet!!) Mood.SPORT else Mood.QUIET
            sportOk -> Mood.SPORT
            quietOk -> Mood.QUIET
            else -> null
        }
    }

    override fun scores(features: AudioFeatures): Map<Mood, Double?> =
        mapOf(Mood.SPORT to sportScore(features), Mood.QUIET to quietScore(features))

    /** 运动：响、低频足、节奏接近 130 BPM。硬条件不满足时返回 null。 */
    fun sportScore(f: AudioFeatures): Double? {
        val bpm = f.bpm ?: return null
        if (bpm !in 105.0..185.0 || f.bpmConfidence < MIN_CONFIDENCE || f.loudness < -12) return null
        return 0.35 * norm(f.loudness, -12.0, -7.0) +
            0.30 * norm(f.lowRatio, 0.2, 0.6) +
            0.20 * bpmFit(bpm, 130.0) +
            0.15 * norm(f.onsetRate, 2.5, 4.5)
    }

    /** 安静：轻、几乎没有低频、音色偏暗。 */
    fun quietScore(f: AudioFeatures): Double? {
        if (f.loudness > -14) return null
        return 0.50 * norm(-f.loudness, 14.0, 26.0) +
            0.25 * norm(-f.lowRatio, -0.35, -0.05) +
            0.25 * norm(-f.centroidHz, -1600.0, -700.0)
    }

    private fun norm(value: Double, low: Double, high: Double) = ((value - low) / (high - low)).coerceIn(0.0, 1.0)

    /** 越接近 [center] 越接近 1，偏离一个八度为 0。 */
    private fun bpmFit(bpm: Double, center: Double) = (1 - abs(log2(bpm / center))).coerceIn(0.0, 1.0)

    private const val MIN_CONFIDENCE = 1.3
}
