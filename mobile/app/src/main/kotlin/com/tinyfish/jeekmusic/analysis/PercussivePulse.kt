package com.tinyfish.jeekmusic.analysis

import kotlin.math.*

data class PulsePeak(val bpm: Int, val correlation: Double, val coherence: Double) {
    val salience get() = max(0.0, correlation) * coherence
}

/** Transient periodicity, not annotated beats or calibrated workout probability. */
data class PercussivePulse(val bass: List<PulsePeak>, val broadband: List<PulsePeak>, val error: String? = null,
    val startMs: Long = 0, val endMs: Long = 0, val attempts: Int = 1, val elapsedMs: Long = 0) {
    val supportsWorkout get() = error == null && bass.any { b -> b.bpm in 95..220 && b.salience >= .025 &&
        broadband.any { a -> a.bpm in 95..220 && a.salience >= .05 && abs(a.bpm-b.bpm) <= 3 } }
}

/** Same bounded HPSS/pulse contract as Windows PercussivePulseExtractor.cs; no learned weights. */
object PercussivePulseExtractor {
    private const val RATE = 11025
    private const val SIZE = 1024
    private const val HOP = 128
    private const val RADIUS = 15
    private const val KERNEL = 31
    private const val FPS = RATE.toDouble() / HOP
    private const val SINC_RADIUS = 64
    private val resampleWeights by lazy {
        Array(441) { phase ->
            val cutoff = (11025.0/16000)*.97
            val weights = DoubleArray(2*SINC_RADIUS+1) { j ->
                val x = j-SINC_RADIUS-phase/441.0
                val z = PI*cutoff*x
                val w = if (abs(x) > SINC_RADIUS) 0.0 else .42+.5*cos(PI*x/SINC_RADIUS)+.08*cos(2*PI*x/SINC_RADIUS)
                cutoff*(if (abs(z) < 1e-12) 1.0 else sin(z)/z)*w
            }
            val sum = weights.sum()
            for (j in weights.indices) weights[j] /= sum
            weights
        }
    }

    fun fromModelPcm(mono16k: FloatArray, checkCancelled: () -> Unit = {}): PercussivePulse {
        checkCancelled()
        require(mono16k.size in 16000*4..16000*31)
        if (mono16k.all { it == 0f }) return PercussivePulse(emptyList(),emptyList())
        val weights = resampleWeights
        val samples = FloatArray((mono16k.size.toLong()*441/640).toInt())
        for (i in samples.indices) {
            if (i%256 == 0) checkCancelled()
            val position = i.toLong()*640
            val center = (position/441).toInt()
            val phase = (position%441).toInt()
            var value = 0.0
            for (k in -SINC_RADIUS..SINC_RADIUS) if (center+k in mono16k.indices)
                value += mono16k[center+k]*weights[phase][k+SINC_RADIUS]
            samples[i] = value.toFloat()
        }
        return extract(samples,checkCancelled)
    }

    fun extract(samples: FloatArray, checkCancelled: () -> Unit = {}): PercussivePulse {
        require(samples.size in RATE*4..RATE*31 && samples.all { it.isFinite() })
        val frames = 1 + samples.size/HOP
        val bins = SIZE/2+1
        val magnitude = DoubleArray(frames*bins)
        val fft = Fft(SIZE)
        val re = DoubleArray(SIZE)
        val im = DoubleArray(SIZE)
        val window = DoubleArray(SIZE) { .5-.5*cos(2*PI*it/SIZE) }
        for (t in 0 until frames) {
            checkCancelled()
            for (i in 0 until SIZE) {
                val index = t*HOP+i-SIZE/2
                re[i] = if (index in samples.indices) samples[index]*window[i] else 0.0
                im[i] = 0.0
            }
            fft.transform(re,im)
            for (k in 0 until bins) magnitude[t*bins+k] = sqrt(re[k]*re[k]+im[k]*im[k])
        }
        val bass = DoubleArray(frames-1)
        val broad = DoubleArray(frames-1)
        val previous = DoubleArray(bins)
        val horizontal = DoubleArray(KERNEL)
        val vertical = DoubleArray(KERNEL)
        val low = ceil(30.0*SIZE/RATE).toInt()
        val bassEnd = ceil(150.0*SIZE/RATE).toInt()
        for (t in 0 until frames) {
            checkCancelled()
            var bassFlux = 0.0
            var broadFlux = 0.0
            for (k in low until bins) {
                for (j in -RADIUS..RADIUS) {
                    horizontal[j+RADIUS] = magnitude[reflect(t+j,frames)*bins+k]
                    vertical[j+RADIUS] = magnitude[t*bins+reflect(k+j,bins)]
                }
                horizontal.sort(); vertical.sort()
                val h = horizontal[RADIUS]
                val p = vertical[RADIUS]
                val denominator = h*h+p*p
                val mask = if (denominator > 0) p*p/denominator else .5
                val value = ln(1+100*magnitude[t*bins+k]*mask)
                val diff = max(0.0,value-previous[k])
                if (k < bassEnd) bassFlux += diff
                broadFlux += diff
                previous[k] = value
            }
            if (t > 0) { bass[t-1] = bassFlux/(bassEnd-low); broad[t-1] = broadFlux/(bins-low) }
        }
        return PercussivePulse(peaks(novelty(bass),checkCancelled),peaks(novelty(broad),checkCancelled))
    }

    private fun reflect(i: Int,n: Int) = if (i < 0) -i-1 else if (i >= n) 2*n-i-1 else i

    private fun novelty(flux: DoubleArray): DoubleArray {
        val prefix = DoubleArray(flux.size+1)
        for (i in flux.indices) prefix[i+1] = prefix[i]+flux[i]
        return DoubleArray(flux.size) { i ->
            val from = i-21
            val to = i+22
            val sum = prefix[min(to,flux.size)]-prefix[max(from,0)]+max(0,-from)*flux[0]+max(0,to-flux.size)*flux.last()
            max(0.0,flux[i]-sum/43)
        }
    }

    private fun peaks(envelope: DoubleArray, checkCancelled: () -> Unit): List<PulsePeak> {
        val total = envelope.sum()
        if (total <= 1e-9) return emptyList()
        val candidates = (40..240).map { bpm ->
            checkCancelled()
            val lag = round(60*FPS/bpm).toInt()
            val count = envelope.size-lag
            var mx = 0.0; var my = 0.0
            for (i in 0 until count) { mx += envelope[i]; my += envelope[i+lag] }
            mx /= count; my /= count
            var xx = 0.0; var yy = 0.0; var xy = 0.0
            for (i in 0 until count) {
                val x = envelope[i]-mx
                val y = envelope[i+lag]-my
                xx += x*x; yy += y*y; xy += x*y
            }
            val denominator = sqrt(xx*yy)
            var real = 0.0; var imaginary = 0.0
            val angle = -2*PI*bpm/(60*FPS)
            val c = cos(angle); val s = sin(angle)
            var cr = 1.0; var ci = 0.0
            for (value in envelope) {
                real += value*cr; imaginary += value*ci
                val nr = cr*c-ci*s
                ci = cr*s+ci*c; cr = nr
            }
            PulsePeak(bpm,if (denominator > 1e-9) (xy/denominator).coerceIn(-1.0,1.0) else 0.0,
                (sqrt(real*real+imaginary*imaginary)/total).coerceIn(0.0,1.0))
        }
        return candidates.filterIndexed { i,c -> c.salience > 0 &&
            (i == 0 || c.salience >= candidates[i-1].salience) &&
            (i == candidates.lastIndex || c.salience > candidates[i+1].salience) }
            .sortedByDescending { it.salience }.take(8)
    }
}
