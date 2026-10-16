package com.tinyfish.jeekmusic.analysis

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** 特征取自校准曲库中的真实歌曲（tools/calibration-pcm.sh）。 */
class MoodClassifierTest {

    private fun features(
        bpm: Double? = 120.0,
        confidence: Double = 7.0,
        loudness: Double = -10.0,
        range: Double = 5.0,
        onsetRate: Double = 3.5,
        centroid: Double = 1300.0,
        lowRatio: Double = 0.4,
    ) = AudioFeatures(bpm, confidence, loudness, range, onsetRate, centroid, lowRatio)

    private fun classify(f: AudioFeatures, s: Sensitivity = Sensitivity.NORMAL) = RuleBasedMoodClassifier.classify(f, s)

    @Test
    fun `舞曲 → 运动`() {
        // Lady Gaga - Poker Face
        assertEquals(Mood.SPORT, classify(features(bpm = 118.0, loudness = -7.9, onsetRate = 3.41, centroid = 1439.0, lowRatio = 0.51)))
        // Justin Bieber - Baby
        assertEquals(Mood.SPORT, classify(features(bpm = 130.0, loudness = -7.3, onsetRate = 3.38, centroid = 1468.0, lowRatio = 0.67)))
    }

    @Test
    fun `抒情歌 → 都不是`() {
        // 周杰伦 - 青花瓷
        assertNull(classify(features(bpm = 108.0, loudness = -9.9, onsetRate = 2.98, centroid = 1016.0, lowRatio = 0.43)))
        // 周杰伦 - 东风破（节奏被检测成了 145，但响度和起音不够）
        assertNull(classify(features(bpm = 145.0, loudness = -11.2, onsetRate = 2.71, centroid = 1083.0, lowRatio = 0.60)))
    }

    @Test
    fun `安静的古典 → 安静`() {
        // 肖邦 夜曲 Op.9 No.2
        assertEquals(Mood.QUIET, classify(features(bpm = 91.0, loudness = -27.8, onsetRate = 2.04, centroid = 563.0, lowRatio = 0.02)))
        // 勃拉姆斯 摇篮曲
        assertEquals(Mood.QUIET, classify(features(bpm = 68.0, loudness = -28.2, onsetRate = 3.14, centroid = 598.0, lowRatio = 0.07)))
    }

    @Test
    fun `明亮激昂的古典 → 不是安静`() {
        // 维瓦尔第《春》：响度适中、音色明亮
        assertNull(classify(features(bpm = 201.0, loudness = -19.4, onsetRate = 4.48, centroid = 1776.0, lowRatio = 0.06)))
    }

    @Test
    fun `节奏快但很轻 → 不是运动`() {
        assertNull(RuleBasedMoodClassifier.sportScore(features(bpm = 140.0, loudness = -20.0, onsetRate = 5.0)))
    }

    @Test
    fun `节奏不在范围内或没有节奏 → 不是运动`() {
        assertNull(RuleBasedMoodClassifier.sportScore(features(bpm = 90.0, loudness = -7.0)))
        assertNull(RuleBasedMoodClassifier.sportScore(features(bpm = null, loudness = -7.0)))
    }

    @Test
    fun `灵敏度影响边界上的歌`() {
        // 0.35·0.5 + 0.30·0.5 + 0.20·1 + 0.15·0.25 ≈ 0.56
        val borderline = features(bpm = 130.0, loudness = -9.5, onsetRate = 3.0, lowRatio = 0.4)
        assertEquals(0.5625, RuleBasedMoodClassifier.sportScore(borderline)!!, 0.01)
        assertEquals(Mood.SPORT, classify(borderline, Sensitivity.LOOSE))
        assertNull(classify(borderline, Sensitivity.NORMAL))
        assertNull(classify(borderline, Sensitivity.STRICT))
    }
}
