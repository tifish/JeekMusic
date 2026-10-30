package com.tinyfish.jeekmusic.analysis

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import org.tensorflow.lite.Interpreter
import java.io.FileInputStream
import java.nio.channels.FileChannel
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.math.ln

/**
 * 用 Google 的 YAMNet 音频分类模型（AudioSet 521 类，Apache 2.0）给歌曲打分（docs/design.md §6.3）。
 * 完全在本地运行；模型约 4MB，放在 assets 里，第一次分析时才加载，分析完释放。
 */
@Singleton
class AudioTagger @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private var interpreter: Interpreter? = null

    /**
     * 16kHz 单声道 PCM → 各类别在所有帧上的平均分（帧长 0.975 秒、不重叠）。不足一帧时返回 null。
     */
    @Synchronized
    fun meanScores(pcm: FloatArray): FloatArray? = analyzeWindows(pcm).first

    @Synchronized
    fun analyzeWindows(pcm: FloatArray, checkCancelled: () -> Unit = {}): Pair<FloatArray?, List<ModelScores>> {
        if (pcm.size < FRAME) return null to emptyList()
        val windows = mutableListOf<ModelScores>()
        val model = interpreter ?: Interpreter(loadModel(), Interpreter.Options().setNumThreads(2)).also { interpreter = it }
        val input = FloatArray(FRAME)
        val output = arrayOf(FloatArray(CLASSES))
        val sum = FloatArray(CLASSES)
        var frames = 0
        var start = 0
        while (start + FRAME <= pcm.size) {
            checkCancelled()
            System.arraycopy(pcm, start, input, 0, FRAME)
            model.run(input, output)
            check(output[0].all { it.isFinite() }) { "Non-finite YAMNet output" }
            windows += scores(output[0])
            for (i in 0 until CLASSES) sum[i] += output[0][i]
            frames++
            start += FRAME
        }
        return FloatArray(CLASSES) { sum[it] / frames } to windows
    }

    /** 释放模型（约 20MB 内存）；分析任务结束时调用。 */
    @Synchronized
    fun close() {
        interpreter?.close()
        interpreter = null
    }

    private fun loadModel() = context.assets.openFd(MODEL).use { fd ->
        FileInputStream(fd.fileDescriptor).channel.map(FileChannel.MapMode.READ_ONLY, fd.startOffset, fd.declaredLength)
    }

    companion object {
        const val SAMPLE_RATE = 16_000
        private const val FRAME = 15_600
        private const val CLASSES = 521
        private const val MODEL = "yamnet.tflite"

        // 类别编号来自 YAMNet 的 yamnet_class_map.csv；分组和阈值见 tools/calibration/ 的校准
        /** Singing, Choir, Child singing, Rapping, Humming, Chant, Vocal music, A capella, Opera, Speech, Lullaby, Synthetic singing */
        private val VOICE = intArrayOf(24, 25, 29, 31, 32, 27, 249, 250, 233, 0, 266, 30)

        /**
         * Musical instrument, Bowed string instrument, Violin, Cello, Piano, Keyboard, Orchestra, String section, Brass instrument,
         * Trumpet, Woodwind instrument, Flute, Double bass, Electric piano, Pizzicato, Plucked string instrument, Guitar
         */
        private val INSTRUMENT = intArrayOf(133, 184, 186, 188, 148, 147, 179, 185, 180, 182, 190, 191, 189, 149, 187, 134, 135)

        /** Classical music, Opera：只用曲风类。钢琴、弦乐等乐器类在流行抒情歌、翻奏和配乐里同样很高，不能当作古典的证据 */
        private val CLASSICAL = intArrayOf(232, 233)

        /**
         * 其他所有曲风类：Pop、Rock、R&B、Jazz、Folk、Electronic、New-age、Music of Asia、Background / Theme / Soundtrack music、
         * Video game、Wedding music 等（211–231、234–248、250–260、262–265、267–270，不含 Vocal music、Song、Lullaby）
         */
        private val POP = ((211..231) + (234..248) + (250..260) + (262..265) + (267..270)).toIntArray()

        /** Tender, Sad, Lullaby, New-age, Ambient, Background music */
        private val CALM = intArrayOf(273, 272, 266, 248, 241, 262)

        /** Exciting, Happy, Dance, Electronic, Hip hop, Rock, Drum kit, Drum, Rapping, Disco, Pop, Electronic dance music */
        private val ENERGY = intArrayOf(274, 271, 269, 234, 212, 214, 157, 159, 31, 231, 211, 240)

        private const val ORCHESTRA = 179

        /** 平均分 → 分类用的几组得分。 */
        fun scores(means: FloatArray): ModelScores {
            fun sum(ids: IntArray) = ids.sumOf { means[it].toDouble() }
            return ModelScores(
                vocal = ln((sum(VOICE) + 1e-3) / (sum(INSTRUMENT) + 1e-3)),
                classical = ln((sum(CLASSICAL) + 1e-3) / (sum(POP) + 1e-3)),
                calm = sum(CALM),
                energy = sum(ENERGY),
                orchestra = means[ORCHESTRA].toDouble(),
            )
        }
    }
}
