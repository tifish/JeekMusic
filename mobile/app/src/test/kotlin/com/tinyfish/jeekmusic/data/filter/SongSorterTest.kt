package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.SortKeys
import com.tinyfish.jeekmusic.data.settings.Settings
import com.tinyfish.jeekmusic.data.settings.SongSort
import com.tinyfish.jeekmusic.util.FileModified
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.time.ZoneId
import java.util.Locale

class SongSorterTest {
    private val preferences = MutableStateFlow(Settings())
    private val sorter = SongSorter(preferences) { SortKeys { it } }
    private val date = 1792828800L // Unix seconds, 2026-10-24 08:00 UTC

    private fun song(id: Long, name: String, modified: Long, cue: Int? = null, track: Int? = null, disc: Int? = null) = TrackEntity(
        id = id, trackKey = "$name#$id", fileKey = name, mediaStoreId = id, volumeName = "mock", folderId = 1,
        relativePath = "Music/", fileName = name, titleTag = null, title = name, album = null, albumArtist = null,
        albumKey = null, artistRaw = null, artistDisplay = "", artistInferred = false, trackNo = track, discNo = disc,
        year = null, genre = null, durationMs = 1000, mimeType = null, bitrate = null, sampleRate = null,
        dateModified = modified, size = 1, hasEmbeddedLyrics = false, cueTrackNo = cue,
    )

    @Test fun missingDefaultsAndSavedChoicesDriveLibraryAndOrderedViews() = runBlocking {
        assertEquals(SongSort.MODIFIED, sorter.sort.first())
        assertEquals(SongSort.FILE_NAME, sorter.orderedViewSort.first())
        assertNull(sorter.albumSort.first())
        for (raw in listOf(null, "DEFAULT", "FILE_NAME", "MODIFIED", "TITLE", "ARTIST", "bad")) {
            val hasPreference = SongSort.hasPreference(raw)
            val value = SongSort.fromSaved(raw)
            preferences.value = Settings(songSort = value, hasSongSortPreference = hasPreference)
            assertEquals(value, sorter.sort.first())
            assertEquals(if (hasPreference) value else SongSort.FILE_NAME, sorter.orderedViewSort.first())
            assertEquals(if (hasPreference) value else null, sorter.albumSort.first())
            if (raw == null || raw == "bad") { assertFalse(hasPreference); assertEquals(SongSort.MODIFIED, value) }
            else { assertTrue(hasPreference); assertEquals(if (raw == "DEFAULT") SongSort.FILE_NAME else SongSort.valueOf(raw), value) }
        }
    }

    @Test fun datesUseSecondsAndUnknownDatesStayLastWithStableFileAndCueTies() {
        val tracks = listOf(song(9, "9.flac", Long.MAX_VALUE), song(8, "8.flac", -1), song(7, "7.flac", 0),
            song(3, "album.flac", date, cue = 2), song(2, "album.flac", date, cue = 1),
            song(1, "old.flac", date - 1), song(4, "new.flac", date + 1))
        val expected = listOf(4L, 2L, 3L, 1L, 7L, 8L, 9L)
        assertEquals(expected, sorter.sorted(tracks, SongSort.MODIFIED) { it }.map { it.id })
        assertEquals(expected, sorter.sorted(tracks.reversed(), SongSort.MODIFIED) { it }.map { it.id })
        val missing: List<TrackEntity?> = listOf(null) + tracks
        assertNull(sorter.sorted(missing, SongSort.MODIFIED) { it }.last())
        assertEquals("2026-10-24 16:00", FileModified.format(date, ZoneId.of("Asia/Singapore"), Locale.US))
        assertEquals("2026-10-24 08:00", FileModified.format(date, ZoneId.of("UTC"), Locale.US))
        assertEquals(FileModified.format(date), FileModified.format(date + 1)) // display minutes, sort real seconds
        for (invalid in listOf(0, -1, Long.MIN_VALUE, Long.MAX_VALUE, date * 1000)) {
            assertEquals("—", FileModified.format(invalid))
        }
    }

    @Test fun albumDefaultKeepsDiscAndTrackOrderWhileExplicitChoiceRemainsGlobal() {
        val tracks = listOf(song(3, "a.flac", date + 2, track = 1, disc = 2),
            song(2, "b.flac", date + 1, track = 2), song(1, "z.flac", date, track = 1))
        assertEquals(listOf(1L, 2L, 3L), sorter.albumSorted(tracks, null).map { it.id })
        assertEquals(listOf(3L, 2L, 1L), sorter.albumSorted(tracks, SongSort.MODIFIED).map { it.id })
        assertEquals(listOf(3L, 2L, 1L), sorter.albumSorted(tracks, SongSort.FILE_NAME).map { it.id })
    }
}
