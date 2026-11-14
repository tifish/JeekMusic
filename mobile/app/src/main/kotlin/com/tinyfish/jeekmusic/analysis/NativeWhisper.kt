package com.tinyfish.jeekmusic.analysis

/** Task-owned native state. close runs only after analyze returns; cancel is concurrent. */
class NativeWhisper(model: String) : AutoCloseable {
    private var handle = open(model)
    fun recognize(pcm: FloatArray, deadline: Long): Triple<String, Double, Boolean> {
        val result = analyze(handle, pcm, deadline).split('\t')
        return Triple(result[0], result[1].toDouble(), result[2].toInt() >= 4)
    }
    @Synchronized fun cancel() { if (handle != 0L) cancelNative(handle) }
    @Synchronized override fun close() { if (handle != 0L) { free(handle); handle = 0 } }
    private external fun open(model: String): Long
    private external fun analyze(handle: Long, pcm: FloatArray, deadline: Long): String
    private external fun cancelNative(handle: Long)
    private external fun free(handle: Long)
    companion object { init { System.loadLibrary("jeek_whisper") } }
}
