package com.tinyfish.jeekmusic.data.cover

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CoverMatcherTest {

    // 单元测试里没有 android.icu，用一个小表模拟繁简转换
    private val matcher = CoverMatcher { text -> text.map { mapOf('陳' to '陈', '紅' to '红', '灣' to '湾')[it] ?: it }.joinToString("") }

    @Test
    fun titleIgnoresCaseBracketsAndSuffixes() {
        assertTrue(matcher.titleMatches("Poker Face", "Poker Face"))
        assertTrue(matcher.titleMatches("Poker Face", "poker face (Live)"))
        assertTrue(matcher.titleMatches("Love Story", "Love Story - Remastered 2019"))
        assertTrue(matcher.titleMatches("Problem (feat. Iggy Azalea)", "Problem"))
        assertTrue(matcher.titleMatches("月牙湾", "月牙灣"))
        assertTrue(matcher.titleMatches("红豆", "紅豆（Live）"))
    }

    @Test
    fun differentTitlesDoNotMatch() {
        assertFalse(matcher.titleMatches("Baby", "Baby Shark"))
        assertFalse(matcher.titleMatches("小雨", "小雨天"))
        assertFalse(matcher.titleMatches("Yesterday", "Today"))
    }

    @Test
    fun anyOfSeveralArtistsMatches() {
        assertTrue(matcher.artistMatches("Ariana Grande,Iggy Azalea", "Ariana Grande"))
        assertTrue(matcher.artistMatches("Jason Mraz&Colbie Caillat", "Jason Mraz & Colbie Caillat"))
        assertTrue(matcher.artistMatches("陈奕迅", "陳奕迅"))
        assertTrue(matcher.artistMatches("F.I.R", "F.I.R."))
        assertTrue(matcher.artistMatches("Bruno Mars", "B.o.B feat. Bruno Mars"))
        assertFalse(matcher.artistMatches("Taylor Swift", "Pinkfong"))
        assertFalse(matcher.artistMatches("", "Taylor Swift"))
    }

    @Test
    fun primaryArtistIsTheFirstOne() {
        assertEquals("Justin Bieber", CoverMatcher.primaryArtist("Justin Bieber,卢达克里斯"))
        assertEquals("Jean-Bernard Pommier", CoverMatcher.primaryArtist("Jean-Bernard Pommier／Hallé Orchestra／Lawrence Foster"))
        assertEquals("黄龄", CoverMatcher.primaryArtist("黄龄"))
    }

    @Test
    fun detectsChinese() {
        assertTrue(CoverMatcher.hasCjk("F.I.R 月牙湾"))
        assertFalse(CoverMatcher.hasCjk("Lady Gaga Poker Face"))
    }
}
