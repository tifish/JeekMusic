package com.tinyfish.jeekmusic.analysis

import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 智能标签的校准报告（docs/design.md §6.4）：对 tools/calibration-pcm.sh 生成的 PCM 提取特征并分类，
 * 结果写入 build/calibration/report.tsv。没有校准数据时（比如 CI）直接跳过。
 */
class MoodCalibrationReport {

    @Test
    fun report() {
        val dir = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "build/calibration") }
            .firstOrNull { it.isDirectory } ?: return
        val files = dir.listFiles { f -> f.name.endsWith(".f32") }?.sortedBy { it.name } ?: return
        if (files.isEmpty()) return

        val lines = mutableListOf("name\tbpm\tconf\tlufs\tlra\tonset\tcentroid\tlow\tsport\tquiet\tmood")
        val counts = mutableMapOf<String, Int>()
        for (file in files) {
            val bytes = file.readBytes()
            val floats = FloatArray(bytes.size / 4)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer().get(floats)
            val f = FeatureExtractor.extract(floats)
            val mood = RuleBasedMoodClassifier.classify(f, Sensitivity.NORMAL)
            counts.merge(mood?.name ?: "-", 1, Int::plus)
            lines += listOf(
                file.nameWithoutExtension,
                "%.1f".format(f.bpm ?: 0.0),
                "%.1f".format(f.bpmConfidence),
                "%.1f".format(f.loudness),
                "%.1f".format(f.loudnessRange),
                "%.2f".format(f.onsetRate),
                "%.0f".format(f.centroidHz),
                "%.2f".format(f.lowRatio),
                RuleBasedMoodClassifier.sportScore(f)?.let { "%.2f".format(it) } ?: "-",
                RuleBasedMoodClassifier.quietScore(f)?.let { "%.2f".format(it) } ?: "-",
                mood?.name ?: "-",
            ).joinToString("\t")
        }
        File(dir, "report.tsv").writeText(lines.joinToString("\n"))
        println("calibration: ${files.size} files, $counts")
    }
}
