package com.tinyfish.jeekmusic.analysis

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** 得分取自校准曲库中真实歌曲的 YAMNet 输出（tools/calibration/）。 */
class TagClassifierTest {

    private val pokerFace = ModelScores(vocal = 2.164, classical = -5.564, calm = 0.0005, energy = 0.1898, orchestra = 0.0)
    private val baby = ModelScores(vocal = 3.530, classical = -5.373, calm = 0.0013, energy = 0.1592, orchestra = 0.0)
    private val starFish = ModelScores(vocal = 0.563, classical = -3.074, calm = 0.0940, energy = 0.0208, orchestra = 0.0023) // 周深 - 星鱼
    private val nocturne = ModelScores(vocal = -3.824, classical = 0.969, calm = 0.0444, energy = 0.0008, orchestra = 0.0) // 肖邦夜曲
    private val gymnopedie = ModelScores(vocal = -4.975, classical = 0.742, calm = 0.0224, energy = 0.0001, orchestra = 0.0626)
    private val hungarianDance = ModelScores(vocal = -3.195, classical = 2.290, calm = 0.0031, energy = 0.0016, orchestra = 0.2530)
    private val jupiter = ModelScores(vocal = -4.195, classical = 0.468, calm = 0.0085, energy = 0.0039, orchestra = 0.1634)
    private val violinCover = ModelScores(vocal = -5.780, classical = -2.630, calm = 0.0042, energy = 0.0065, orchestra = 0.0026) // 小提琴翻奏流行歌

    @Test
    fun `舞曲 → 运动，慢歌和慢板 → 安静`() {
        assertEquals(Tag.SPORT, TagClassifier.mood(pokerFace, Sensitivity.NORMAL))
        assertEquals(Tag.SPORT, TagClassifier.mood(baby, Sensitivity.NORMAL))
        assertEquals(Tag.QUIET, TagClassifier.mood(starFish, Sensitivity.NORMAL))
        assertEquals(Tag.QUIET, TagClassifier.mood(nocturne, Sensitivity.NORMAL))
        assertEquals(Tag.QUIET, TagClassifier.mood(gymnopedie, Sensitivity.NORMAL))
    }

    @Test
    fun `大编制管弦乐的快板不算安静`() {
        assertNull(TagClassifier.mood(hungarianDance, Sensitivity.NORMAL))
        assertNull(TagClassifier.mood(jupiter, Sensitivity.NORMAL))
    }

    @Test
    fun `灵敏度越严格，归入标签的歌越少`() {
        // 星鱼的"安静"得分约 1.4：严格档也算；轻柔但不明显的歌只有宽松档才算
        val mild = ModelScores(vocal = 1.0, classical = -1.0, calm = 0.03, energy = 0.025, orchestra = 0.0)
        assertEquals(Tag.QUIET, TagClassifier.mood(mild, Sensitivity.LOOSE))
        assertNull(TagClassifier.mood(mild, Sensitivity.STRICT))
        assertEquals(Tag.QUIET, TagClassifier.mood(starFish, Sensitivity.STRICT))
    }

    @Test
    fun 人声和纯音乐() {
        assertEquals(Tag.VOCAL, TagClassifier.voice(pokerFace))
        assertEquals(Tag.VOCAL, TagClassifier.voice(starFish))
        assertEquals(Tag.INSTRUMENTAL, TagClassifier.voice(nocturne))
        assertEquals(Tag.INSTRUMENTAL, TagClassifier.voice(violinCover))
    }

    @Test
    fun 古典和非古典() {
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.genre(pokerFace))
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.genre(starFish))
        assertEquals(Tag.CLASSICAL, TagClassifier.genre(nocturne))
        assertEquals(Tag.CLASSICAL, TagClassifier.genre(jupiter))
        // 爵士、配乐等以前"都不是"的，现在算非古典
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.genre(ModelScores(vocal = -3.0, classical = -1.0, calm = 0.01, energy = 0.01, orchestra = 0.0)))
    }

    @Test
    fun `旧版本保存的流行读成非古典`() {
        assertEquals(Tag.NON_CLASSICAL, Tag.of(TagDimension.GENRE, "POP"))
        assertEquals(Tag.CLASSICAL, Tag.of(TagDimension.GENRE, "CLASSICAL"))
        assertNull(Tag.of(TagDimension.MOOD, "POP"))
        assertEquals(listOf(Tag.CLASSICAL, Tag.NON_CLASSICAL), Tag.of(TagDimension.GENRE))
    }

    @Test
    fun `弦乐编曲的流行歌和小提琴翻奏不算古典`() {
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.genre(violinCover))
        // 区瑞强 - 偏偏喜欢你：旧算法（钢琴、弦乐也算古典）得分 0.82，被判为古典
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.genre(ModelScores(vocal = -1.30, classical = -2.918, calm = 0.05, energy = 0.0, orchestra = 0.0)))
    }

    @Test
    fun `重新分析之前沿用旧的古典阈值`() {
        fun features(version: Int, classical: Double) = com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity(
            trackKey = "k", signature = "s", analyzerVersion = version, bpm = null, bpmConfidence = 0.0, loudness = -20.0,
            loudnessRange = 5.0, onsetRate = 1.0, centroidHz = 1000.0, lowRatio = 0.3, analyzedAt = 0,
            vocalScore = -4.0, classicalScore = classical, calmLevel = 0.01, energyLevel = 0.01, orchestraLevel = 0.0,
        )
        // v3 的得分 0.0 在旧阈值下是非古典，在新阈值下是古典
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.classify(features(3, 0.0), Sensitivity.NORMAL)[TagDimension.GENRE])
        assertEquals(Tag.CLASSICAL, TagClassifier.classify(features(4, 0.0), Sensitivity.NORMAL)[TagDimension.GENRE])
    }

    @Test
    fun `没有模型得分时只用旧规则判断运动和安静`() {
        val old = com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity(
            trackKey = "k", signature = "s", analyzerVersion = 2, bpm = 118.0, bpmConfidence = 7.0, loudness = -7.9,
            loudnessRange = 5.0, onsetRate = 3.41, centroidHz = 1439.0, lowRatio = 0.51, analyzedAt = 0,
        )
        val tags = TagClassifier.classify(old, Sensitivity.NORMAL)
        assertEquals(Tag.SPORT, tags[TagDimension.MOOD])
        assertNull(tags[TagDimension.VOICE])
        assertNull(tags[TagDimension.GENRE])
    }

    @Test
    fun 抽取采样率() {
        val input = FloatArray(16_000) { if (it % 2 == 0) 1f else -1f }
        val out = PcmDecoder.downsample(input, 16_000, 11_025)
        assertEquals(11_025, out.size)
        // 盒式平均会把奈奎斯特附近的高频平均掉
        assert(out.all { kotlin.math.abs(it) <= 1f })
        assertEquals(input.size, PcmDecoder.downsample(input, 16_000, 16_000).size)
    }
}
