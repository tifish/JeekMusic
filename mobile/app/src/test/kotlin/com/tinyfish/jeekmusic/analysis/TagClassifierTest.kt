package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import com.tinyfish.jeekmusic.dbl
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.flag
import com.tinyfish.jeekmusic.str
import com.tinyfish.jeekmusic.strings
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/tag-classifier.json（两端共用），得分取自校准曲库中真实歌曲的 YAMNet 输出（tools/calibration/）。 */
class TagClassifierTest {

    private val file = SharedCases.load("tag-classifier.json")
    private val scores = file.getJSONObject("scores").let { o ->
        o.keys().asSequence().associateWith {
            val s = o.getJSONObject(it)
            ModelScores(s.getDouble("vocal"), s.getDouble("classical"), s.getDouble("calm"), s.getDouble("energy"), s.getDouble("orchestra"))
        }
    }

    private fun scores(c: JSONObject) = scores.getValue(c.getString("scores"))

    @Test
    fun mood() {
        for (c in file.cases("mood")) {
            assertEquals(c.description(), Tag.valueOf(c.getString("expected")), TagClassifier.mood(scores(c), Sensitivity.valueOf(c.getString("sensitivity"))))
        }
    }

    @Test
    fun voiceAndGenre() {
        for (c in file.cases("voice")) assertEquals(c.description(), Tag.valueOf(c.getString("expected")), TagClassifier.voice(scores(c)))
        for (c in file.cases("genre")) assertEquals(c.description(), Tag.valueOf(c.getString("expected")), TagClassifier.genre(scores(c)))
    }

    @Test
    fun classify() {
        for (c in file.cases("classify")) {
            val f = c.getJSONObject("features")
            val features = TrackFeaturesEntity(
                trackKey = "k", signature = "s", analyzerVersion = f.getInt("analyzerVersion"), bpm = f.dbl("bpm"),
                bpmConfidence = f.getDouble("bpmConfidence"), loudness = f.getDouble("loudness"), loudnessRange = f.getDouble("loudnessRange"),
                onsetRate = f.getDouble("onsetRate"), centroidHz = f.getDouble("centroidHz"), lowRatio = f.getDouble("lowRatio"), analyzedAt = 0,
                vocalScore = f.dbl("vocalScore"), classicalScore = f.dbl("classicalScore"), calmLevel = f.dbl("calmLevel"),
                energyLevel = f.dbl("energyLevel"), orchestraLevel = f.dbl("orchestraLevel"),
            )
            val tags = TagClassifier.classify(features, Sensitivity.valueOf(c.getString("sensitivity")), classicalTitle = c.flag("classicalTitle"))
            val expected = c.getJSONObject("expected")
            for (dimension in expected.keys()) {
                assertEquals("${c.description()} $dimension", expected.str(dimension)?.let(Tag::valueOf), tags[TagDimension.valueOf(dimension)])
            }
        }
    }

    @Test
    fun tagOf() {
        for (c in file.cases("tagOf")) {
            assertEquals(c.description(), c.str("expected")?.let(Tag::valueOf), Tag.of(TagDimension.valueOf(c.getString("dimension")), c.str("value")))
        }
        for (c in file.cases("tagsOfDimension")) {
            assertEquals(c.description(), c.getJSONArray("expected").strings(), Tag.of(TagDimension.valueOf(c.getString("dimension"))).map { it.name })
        }
    }

    /** 不适合写成 JSON：抽取采样率（PcmDecoder.downsample）。 */
    @Test
    fun `抽取采样率`() {
        val input = FloatArray(16_000) { if (it % 2 == 0) 1f else -1f }
        val out = PcmDecoder.downsample(input, 16_000, 11_025)
        assertEquals(11_025, out.size)
        // 盒式平均会把奈奎斯特附近的高频平均掉
        assert(out.all { kotlin.math.abs(it) <= 1f })
        assertEquals(input.size, PcmDecoder.downsample(input, 16_000, 16_000).size)
    }
}
