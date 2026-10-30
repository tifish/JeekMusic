package com.tinyfish.jeekmusic.analysis

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.log2
import kotlin.math.max
import kotlin.math.sqrt

/** 一首歌的音频特征（docs/design.md §6.3）。 */
data class AudioFeatures(
    val bpm: Double?,
    val bpmConfidence: Double,
    /** 相对响度代理（非标准 LUFS）。 */
    val loudness: Double,
    /** 分位响度差代理（非标准 LRA）。 */
    val loudnessRange: Double,
    /** 每秒起音数。 */
    val onsetRate: Double,
    val centroidHz: Double,
    /** 150Hz 以下能量占比。 */
    val lowRatio: Double,
)

/**
 * 从单声道 PCM（[-1, 1]）中提取特征。帧长 1024、帧移 256、汉宁窗。
 * v5 使用最长的独立已解码片段，避免接缝制造起音；不据此推断整曲标准响度。
 */
object FeatureExtractor {

    const val SAMPLE_RATE = 11_025
    private const val FRAME = 1024
    private const val HOP = 256
    private const val FPS = SAMPLE_RATE.toDouble() / HOP

    fun extract(samples: FloatArray, sampleRate: Int = SAMPLE_RATE): AudioFeatures {
        require(sampleRate == SAMPLE_RATE) { "resample to $SAMPLE_RATE Hz first" }
        val frames = if (samples.size < FRAME) 0 else (samples.size - FRAME) / HOP + 1
        if (frames < 8) return AudioFeatures(null, 0.0, -70.0, 0.0, 0.0, 0.0, 0.0)

        val fft = Fft(FRAME)
        val window = DoubleArray(FRAME) { 0.5 - 0.5 * cos(2 * PI * it / (FRAME - 1)) }
        val bins = FRAME / 2 + 1
        val binHz = SAMPLE_RATE.toDouble() / FRAME
        val lowBin = (150 / binHz).toInt()

        val flux = DoubleArray(frames)
        var previous = DoubleArray(bins)
        var centroidSum = 0.0
        var centroidFrames = 0
        var lowEnergy = 0.0
        var totalEnergy = 0.0
        val re = DoubleArray(FRAME)
        val im = DoubleArray(FRAME)

        for (f in 0 until frames) {
            val offset = f * HOP
            for (i in 0 until FRAME) {
                re[i] = samples[offset + i] * window[i]
                im[i] = 0.0
            }
            fft.transform(re, im)
            val current = DoubleArray(bins)
            var magSum = 0.0
            var weighted = 0.0
            var fluxSum = 0.0
            for (k in 0 until bins) {
                val power = re[k] * re[k] + im[k] * im[k]
                val mag = sqrt(power)
                // 对数压缩后的幅度做频谱通量，对响度不敏感
                val compressed = ln(1 + 100 * mag)
                current[k] = compressed
                val diff = compressed - previous[k]
                if (diff > 0) fluxSum += diff
                magSum += mag
                weighted += mag * k * binHz
                if (k <= lowBin) lowEnergy += power
                totalEnergy += power
            }
            flux[f] = fluxSum
            if (magSum > 1e-3) {
                centroidSum += weighted / magSum
                centroidFrames++
            }
            previous = current
        }
        flux[0] = 0.0

        val envelope = onsetEnvelope(flux)
        val (bpm, confidence) = tempo(envelope)
        val (loudness, range) = loudness(samples)
        return AudioFeatures(
            bpm = bpm,
            bpmConfidence = confidence,
            loudness = loudness,
            loudnessRange = range,
            onsetRate = onsetRate(envelope),
            centroidHz = if (centroidFrames > 0) centroidSum / centroidFrames else 0.0,
            lowRatio = if (totalEnergy > 0) lowEnergy / totalEnergy else 0.0,
        )
    }

    /** 减去约 0.5 秒的滑动平均后半波整流，突出起音。 */
    private fun onsetEnvelope(flux: DoubleArray): DoubleArray {
        val radius = (FPS / 4).toInt()
        val prefix = DoubleArray(flux.size + 1)
        for (i in flux.indices) prefix[i + 1] = prefix[i] + flux[i]
        return DoubleArray(flux.size) { i ->
            val from = max(0, i - radius)
            val to = minOf(flux.size, i + radius + 1)
            val mean = (prefix[to] - prefix[from]) / (to - from)
            max(0.0, flux[i] - mean)
        }
    }

    /**
     * 对起音包络做自相关，在 60–200 BPM 的范围内找峰值；乘以以 120 BPM 为中心的对数高斯权重减少倍频误判。
     * 置信度 = 峰值相对于所有候选的平均值的倍数。
     */
    private fun tempo(envelope: DoubleArray): Pair<Double?, Double> {
        val minLag = (60 * FPS / 200).toInt()
        val maxLag = (60 * FPS / 60).toInt() + 1
        if (envelope.size < maxLag * 4) return null to 0.0
        val mean = envelope.average()
        val centered = DoubleArray(envelope.size) { envelope[it] - mean }
        val acf = DoubleArray(maxLag + 2)
        for (lag in minLag - 1..maxLag + 1) {
            var sum = 0.0
            for (i in 0 until centered.size - lag) sum += centered[i] * centered[i + lag]
            acf[lag] = sum / (centered.size - lag)
        }
        var bestLag = -1
        var bestScore = Double.NEGATIVE_INFINITY
        var scoreSum = 0.0
        var count = 0
        for (lag in minLag..maxLag) {
            val bpm = 60 * FPS / lag
            val weight = exp(-0.5 * (log2(bpm / 120) / 1.0).let { it * it })
            val score = acf[lag] * weight
            scoreSum += max(0.0, score)
            count++
            if (score > bestScore) {
                bestScore = score
                bestLag = lag
            }
        }
        if (bestLag < 0 || bestScore <= 0) return null to 0.0
        // 抛物线插值求小数延迟
        val y0 = acf[bestLag - 1]
        val y1 = acf[bestLag]
        val y2 = acf[bestLag + 1]
        val denom = y0 - 2 * y1 + y2
        val shift = if (denom != 0.0) (0.5 * (y0 - y2) / denom).coerceIn(-0.5, 0.5) else 0.0
        val bpm = 60 * FPS / (bestLag + shift)
        val confidence = bestScore / (scoreSum / count).coerceAtLeast(1e-9)
        return bpm to confidence
    }

    /** 起音包络的峰值：高于"均值 + 0.5 倍标准差"、且与上一个峰相隔至少 0.1 秒。 */
    private fun onsetRate(envelope: DoubleArray): Double {
        val mean = envelope.average()
        val std = sqrt(envelope.sumOf { (it - mean) * (it - mean) } / envelope.size)
        val threshold = mean + 0.5 * std
        val minGap = (FPS * 0.1).toInt()
        var last = -minGap
        var peaks = 0
        for (i in 1 until envelope.size - 1) {
            val v = envelope[i]
            if (v > threshold && v >= envelope[i - 1] && v > envelope[i + 1] && i - last >= minGap) {
                peaks++
                last = i
            }
        }
        return peaks / (envelope.size / FPS)
    }

    /**
     * 相对响度代理（非标准 LUFS）：400ms 块的均方，绝对门限 -70、相对门限 -10（ITU-R BS.1770 的门限积分，省略 K 计权的高架滤波）。
     * 动态范围：3 秒短时响度的第 10 到第 95 百分位之差（相对门限 -20）。
     */
    private fun loudness(samples: FloatArray): Pair<Double, Double> {
        val block = (SAMPLE_RATE * 0.4).toInt()
        val step = block / 4
        val blocks = mutableListOf<Double>()
        var start = 0
        while (start + block <= samples.size) {
            var sum = 0.0
            for (i in start until start + block) sum += samples[i].toDouble() * samples[i]
            blocks += sum / block
            start += step
        }
        fun lufs(ms: Double) = -0.691 + 10 * log10(ms.coerceAtLeast(1e-12))
        val absolute = blocks.filter { lufs(it) > -70 }
        if (absolute.isEmpty()) return -70.0 to 0.0
        val ungated = lufs(absolute.average())
        val gated = absolute.filter { lufs(it) > ungated - 10 }
        val integrated = lufs((gated.ifEmpty { absolute }).average())

        val shortBlock = SAMPLE_RATE * 3
        val shortStep = SAMPLE_RATE / 2
        val shortTerm = mutableListOf<Double>()
        start = 0
        while (start + shortBlock <= samples.size) {
            var sum = 0.0
            for (i in start until start + shortBlock) sum += samples[i].toDouble() * samples[i]
            shortTerm += lufs(sum / shortBlock)
            start += shortStep
        }
        val usable = shortTerm.filter { it > -70 && it > integrated - 20 }.sorted()
        val range = if (usable.size >= 2) {
            usable[(usable.lastIndex * 0.95).toInt()] - usable[(usable.lastIndex * 0.10).toInt()]
        } else {
            0.0
        }
        return integrated to range
    }
}
