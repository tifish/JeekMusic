package com.tinyfish.jeekmusic.analysis

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

/** 原地 radix-2 FFT。[re] 和 [im] 的长度必须相同且是 2 的幂。 */
class Fft(private val size: Int) {
    init {
        require(size > 0 && size and (size - 1) == 0) { "size must be a power of two" }
    }

    private val cosTable = DoubleArray(size / 2) { cos(-2 * PI * it / size) }
    private val sinTable = DoubleArray(size / 2) { sin(-2 * PI * it / size) }
    private val bits = Integer.numberOfTrailingZeros(size)

    fun transform(re: DoubleArray, im: DoubleArray) {
        for (i in 0 until size) {
            val j = Integer.reverse(i) ushr (32 - bits)
            if (j > i) {
                var t = re[i]; re[i] = re[j]; re[j] = t
                t = im[i]; im[i] = im[j]; im[j] = t
            }
        }
        var len = 2
        while (len <= size) {
            val half = len / 2
            val step = size / len
            for (start in 0 until size step len) {
                for (k in 0 until half) {
                    val c = cosTable[k * step]
                    val s = sinTable[k * step]
                    val a = start + k
                    val b = a + half
                    val tr = re[b] * c - im[b] * s
                    val ti = re[b] * s + im[b] * c
                    re[b] = re[a] - tr
                    im[b] = im[a] - ti
                    re[a] += tr
                    im[a] += ti
                }
            }
            len *= 2
        }
    }
}
