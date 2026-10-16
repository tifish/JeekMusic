package com.tinyfish.jeekmusic.analysis

import android.content.Context
import android.media.AudioFormat
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.net.Uri
import dagger.hilt.android.qualifiers.ApplicationContext
import java.nio.ByteOrder
import javax.inject.Inject

/**
 * 解码用于分析的 PCM（docs/design.md §6.2）：用系统解码器解码几个片段，下混为单声道、抽取到 11025Hz。
 * 只用于分析，与播放无关。
 */
class PcmDecoder @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    /** 取样窗口：时长 ≥ 40 秒时取 30%、50%、70% 处各 10 秒，否则整首。时间都相对于 [startMs]。 */
    fun windows(startMs: Long, durationMs: Long): List<LongRange> =
        if (durationMs < 40_000) {
            listOf(startMs until startMs + durationMs.coerceAtMost(60_000))
        } else {
            listOf(0.3, 0.5, 0.7).map { p ->
                val from = startMs + (durationMs * p).toLong()
                from until from + 10_000
            }
        }

    /**
     * 失败（格式不支持、文件损坏）时返回 null。
     * 有些文件在 MediaExtractor 中跳转不可靠（实测某个 Ogg Vorbis 跳转后直接越过了窗口），
     * 按窗口解码得不到数据时，退回到不跳转、从头解码 30 秒。
     */
    fun decode(uri: Uri, windowsMs: List<LongRange>): FloatArray? =
        decodeWindows(uri, windowsMs) ?: decodeWindows(uri, listOf(0L until 30_000L), seek = false)

    private fun decodeWindows(uri: Uri, windowsMs: List<LongRange>, seek: Boolean = true): FloatArray? = runCatching {
        val extractor = MediaExtractor()
        try {
            extractor.setDataSource(context, uri, null)
            val track = (0 until extractor.trackCount).firstOrNull {
                extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true
            } ?: return null
            extractor.selectTrack(track)
            val format = extractor.getTrackFormat(track)
            val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            try {
                codec.configure(format, null, null, 0)
                codec.start()
                val out = FloatList()
                for (window in windowsMs) decodeWindow(extractor, codec, window, out, seek)
                out.toArray().takeIf { it.isNotEmpty() }
            } finally {
                codec.release()
            }
        } finally {
            extractor.release()
        }
    }.getOrNull()

    private fun decodeWindow(extractor: MediaExtractor, codec: MediaCodec, window: LongRange, out: FloatList, seek: Boolean) {
        val startUs = window.first * 1000
        val endUs = (window.last + 1) * 1000
        if (seek) extractor.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        codec.flush()

        var outputFormat = codec.outputFormat
        val mono = FloatList()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var guard = 0
        while (guard++ < 100_000) {
            if (!inputDone) {
                val inIndex = codec.dequeueInputBuffer(TIMEOUT_US)
                if (inIndex >= 0) {
                    val buffer = codec.getInputBuffer(inIndex)!!
                    val size = extractor.readSampleData(buffer, 0)
                    val time = extractor.sampleTime
                    if (size < 0 || time > endUs) {
                        codec.queueInputBuffer(inIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                        inputDone = true
                    } else {
                        codec.queueInputBuffer(inIndex, 0, size, time, 0)
                        extractor.advance()
                    }
                }
            }
            val outIndex = codec.dequeueOutputBuffer(info, TIMEOUT_US)
            when {
                outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> outputFormat = codec.outputFormat
                outIndex >= 0 -> {
                    if (info.size > 0 && info.presentationTimeUs in startUs until endUs) {
                        val buffer = codec.getOutputBuffer(outIndex)!!.order(ByteOrder.nativeOrder())
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        appendMono(buffer, outputFormat, mono)
                    }
                    codec.releaseOutputBuffer(outIndex, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                }
            }
        }
        val sourceRate = outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
        resample(mono.toArray(), sourceRate, out)
    }

    private fun appendMono(buffer: java.nio.ByteBuffer, format: MediaFormat, out: FloatList) {
        val channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
        // 下混时除以 √声道数（功率守恒）：对声道高度相关的音乐，响度与 BS.1770 的"各声道功率之和"一致（ffmpeg 也是这样下混的）
        val scale = 1f / kotlin.math.sqrt(channels.toFloat())
        val encoding = if (format.containsKey(MediaFormat.KEY_PCM_ENCODING)) format.getInteger(MediaFormat.KEY_PCM_ENCODING) else AudioFormat.ENCODING_PCM_16BIT
        if (encoding == AudioFormat.ENCODING_PCM_FLOAT) {
            val floats = buffer.asFloatBuffer()
            while (floats.remaining() >= channels) {
                var sum = 0f
                repeat(channels) { sum += floats.get() }
                out.add(sum * scale)
            }
        } else {
            val shorts = buffer.asShortBuffer()
            while (shorts.remaining() >= channels) {
                var sum = 0f
                repeat(channels) { sum += shorts.get() / 32768f }
                out.add(sum * scale)
            }
        }
    }

    /** 抽取到 11025Hz：每个输出样本取对应区间的平均值（简单的盒式低通，足够用于统计特征）。 */
    private fun resample(input: FloatArray, sourceRate: Int, out: FloatList) {
        val target = FeatureExtractor.SAMPLE_RATE
        if (sourceRate == target) {
            input.forEach(out::add)
            return
        }
        val ratio = sourceRate.toDouble() / target
        val count = (input.size / ratio).toInt()
        for (i in 0 until count) {
            val from = (i * ratio).toInt()
            val to = minOf(input.size, ((i + 1) * ratio).toInt().coerceAtLeast(from + 1))
            var sum = 0f
            for (j in from until to) sum += input[j]
            out.add(sum / (to - from))
        }
    }

    /** 不装箱的可增长 float 数组。 */
    private class FloatList {
        private var data = FloatArray(1 shl 16)
        private var size = 0

        fun add(value: Float) {
            if (size == data.size) data = data.copyOf(size * 2)
            data[size++] = value
        }

        fun toArray(): FloatArray = data.copyOf(size)
    }

    private companion object {
        const val TIMEOUT_US = 10_000L
    }
}
