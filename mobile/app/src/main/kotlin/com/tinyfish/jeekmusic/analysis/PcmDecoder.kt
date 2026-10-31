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
 * 解码用于分析的 PCM（docs/design.md §6.2）：用系统解码器解码几个片段，下混为单声道、抽取到 [rate]
 * （YAMNet 需要 16kHz）。只用于分析，与播放无关。
 */
class PcmDecoder @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    fun windows(startMs: Long, durationMs: Long) = PcmContract.windows(startMs, durationMs)

    // Fail closed: never substitute the physical file head for a CUE subtrack.
    fun decode(uri: Uri, windowsMs: List<LongRange>, rate: Int = AudioTagger.SAMPLE_RATE,
        checkCancelled: () -> Unit = {}): FloatArray? = decodeWindows(uri, windowsMs, rate, checkCancelled)

    private fun decodeWindows(uri: Uri, windowsMs: List<LongRange>, rate: Int, checkCancelled: () -> Unit): FloatArray? {
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
                for ((index, window) in windowsMs.withIndex())
                    decodeWindow(extractor, codec, window, rate, out, checkCancelled, flush = index > 0)
                return out.toArray().takeIf { it.isNotEmpty() }
            } finally {
                codec.release()
            }
        } finally {
            extractor.release()
        }
    }

    private fun decodeWindow(extractor: MediaExtractor, codec: MediaCodec, window: LongRange, rate: Int, out: FloatList, checkCancelled: () -> Unit, flush: Boolean) {
        val startUs = window.first * 1000
        val endUs = (window.last + 1) * 1000
        // Compressed extractors can land after the requested time despite
        // PREVIOUS_SYNC (observed with long Vorbis recordings). Decode a short
        // preroll, then retain only timestamp-bounded PCM below; never pad gaps.
        extractor.seekTo((startUs - 1_000_000).coerceAtLeast(0), MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        // A fresh codec has no queued media to discard. Flushing before its first
        // output can discard codec-specific data (notably Vorbis) before delivery.
        // Later seeks follow a drained EOS and must flush the previous stream.
        if (flush) codec.flush()

        var outputFormat = codec.outputFormat
        val mono = FloatList()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var guard = 0
        val deadline = android.os.SystemClock.elapsedRealtime() + 45_000
        while (guard++ < 100_000) {
            checkCancelled()
            check(android.os.SystemClock.elapsedRealtime() < deadline) { "decode_timeout" }
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
                    if (info.size > 0) {
                        val buffer = codec.getOutputBuffer(outIndex)!!.order(ByteOrder.nativeOrder())
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        val channels = outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                        val sampleRate = outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                        val floatPcm = outputFormat.containsKey(MediaFormat.KEY_PCM_ENCODING) &&
                            outputFormat.getInteger(MediaFormat.KEY_PCM_ENCODING) == AudioFormat.ENCODING_PCM_FLOAT
                        val frameBytes = channels * if (floatPcm) 4 else 2
                        val total = info.size / frameBytes
                        fun frameAt(time: Long) = kotlin.math.ceil((time - info.presentationTimeUs) * sampleRate / 1_000_000.0).toInt().coerceIn(0, total)
                        val first = frameAt(startUs)
                        val last = frameAt(endUs)
                        if (last > first) {
                            buffer.position(info.offset + first * frameBytes)
                            buffer.limit(info.offset + last * frameBytes)
                            appendMono(buffer, outputFormat, mono)
                        }
                    }
                    codec.releaseOutputBuffer(outIndex, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                }
            }
        }
        val sourceRate = outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
        val decoded = mono.toArray()
        // Timestamp rounding may expose one extra source sample at the right boundary.
        val count = minOf(decoded.size, ((endUs - startUs) * sourceRate / 1_000_000).toInt())
        downsample(decoded.copyOf(count), sourceRate, rate).forEach(out::add)
    }

    private fun appendMono(buffer: java.nio.ByteBuffer, format: MediaFormat, out: FloatList) {
        val channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
        // Arithmetic mean, finite and bounded, identical to desktop v5.
        val scale = 1.0 / channels
        val encoding = if (format.containsKey(MediaFormat.KEY_PCM_ENCODING)) format.getInteger(MediaFormat.KEY_PCM_ENCODING) else AudioFormat.ENCODING_PCM_16BIT
        if (encoding == AudioFormat.ENCODING_PCM_FLOAT) {
            val floats = buffer.asFloatBuffer()
            while (floats.remaining() >= channels) {
                var sum = 0.0
                repeat(channels) { sum += PcmContract.sample(floats.get()) }
                out.add((sum * scale).toFloat())
            }
        } else {
            val shorts = buffer.asShortBuffer()
            while (shorts.remaining() >= channels) {
                var sum = 0.0
                repeat(channels) { sum += shorts.get() / 32768f }
                out.add((sum * scale).toFloat())
            }
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

    companion object {
        private const val TIMEOUT_US = 10_000L

        fun downsample(input: FloatArray, sourceRate: Int, target: Int): FloatArray =
            PcmContract.resample(input, sourceRate, target)
    }
}
