package com.tinyfish.jeekmusic.analysis

import com.tinyfish.jeekmusic.data.db.TrackFeaturesEntity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ClassicalTitlesTest {

    @Test
    fun catalogNumbersFormsAndMovements() {
        listOf(
            "Nocturne op. 9 no 2 Eb min",
            "Orchestral Suite No. 3 in D major, BWV 1068",
            "Symphony No. 101 in D 'Clock'： II.      Andante",
            "Concerto No.3 in G major BWV.1048：1. (ohne Satzbezeichnung)",
            "Piano Concerto No. 2 in C minor, Op. 18： I. Moderato",
            "Guten Abend, gut' Nacht, Op. 49, No. 4",
            "Lieder：Heidenröslein D257",
            "Mozart： Piano Concerto No.23 in A, K.488 - 2. Adagio",
            "Violin Concerto in F Minor, RV 297 \"L'inverno\"",
            "Reinecke Sonata Op. 167 'Undine' 1. Allegro",
            "帕格尼尼主题狂想曲·作品43号",
            "贝多芬第九交响曲",
            "G大调第一大提琴组曲：前奏曲",
        ).forEach { assertTrue(it, ClassicalTitles.matches(it)) }
    }

    @Test
    fun albumNameAlsoCounts() {
        assertTrue(ClassicalTitles.matches("Clarinet Sonata", album = "Brahms Sonatas Op.120"))
    }

    /** 中文流行歌常用的曲式词、单独的速度词或曲式词都不算。 */
    @Test
    fun popTitlesDoNotMatch() {
        listOf(
            "夜曲", "月半小夜曲", "刻奇主义变奏曲", "狂想曲", "序曲", "爱的圆舞曲", "七里香",
            "Presto", "Symphony", "Baby", "Love The Way You Lie (Part II)", "Boléro", "K歌之王", "Open Arms", "Mass Effect",
        ).forEach { assertFalse(it, ClassicalTitles.matches(it)) }
    }

    @Test
    fun titleRescuesWhatTheModelMissed() {
        val f = TrackFeaturesEntity(
            trackKey = "k", signature = "s", analyzerVersion = 4, bpm = null, bpmConfidence = 0.0, loudness = -20.0,
            loudnessRange = 5.0, onsetRate = 1.0, centroidHz = 1000.0, lowRatio = 0.3, analyzedAt = 0,
            vocalScore = -4.0, classicalScore = -2.0, calmLevel = 0.01, energyLevel = 0.01, orchestraLevel = 0.0,
        )
        assertEquals(Tag.NON_CLASSICAL, TagClassifier.classify(f, Sensitivity.NORMAL)[TagDimension.GENRE])
        assertEquals(Tag.CLASSICAL, TagClassifier.classify(f, Sensitivity.NORMAL, classicalTitle = true)[TagDimension.GENRE])
    }
}
