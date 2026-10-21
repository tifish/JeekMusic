package com.tinyfish.jeekmusic.data.sync

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import com.tinyfish.jeekmusic.objects
import com.tinyfish.jeekmusic.strings
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** 多设备同步的合并（需求 B-7）：两端共用的用例 shared/testcases/sync-merge.json。 */
class SyncMergeTest {
    @Test
    fun partialNavigationKeepsBuiltinPagesAndCanonicalArtistKeys() {
        val normalized = SyncFormat.normalizeNavigation(listOf(NavRef("PLAYLISTS", ""),
            NavRef("ARTIST", " Test   Artist "), NavRef("ARTIST", "test artist"),
            NavRef("ARTIST", " "), NavRef("UNKNOWN", null)))
        assertEquals(listOf(NavRef("PLAYLISTS", null), NavRef("ARTIST", "test artist"),
            NavRef("FOLDERS", null), NavRef("ARTISTS", null)), normalized)
        assertEquals(normalized, SyncFormat.normalizeNavigation(normalized))
        assertEquals(3, SyncFormat.normalizeNavigation(emptyList()).size)
    }

    @Test
    fun normalizationKeepsNewestDimensionAndReset() {
        val state = SyncState(tagOverrides = mapOf(
            "parent/song.flac" to mapOf("MOOD" to Stamped("SPORT", 10), "GENRE" to Stamped("CLASSICAL", 30)),
            "song.flac" to mapOf("MOOD" to Stamped(null, 20))),
            lyricsOffsets = mapOf("parent/song.flac" to Stamped(500L, 30), "song.flac" to Stamped(0L, 10)))
        val normalized = SyncMerge.normalizeReferences(state) { it.substringAfterLast('/') }
        assertEquals(1, normalized.tagOverrides.size)
        assertNull(normalized.tagOverrides["song.flac"]!!["MOOD"]!!.value)
        assertEquals("CLASSICAL", normalized.tagOverrides["song.flac"]!!["GENRE"]!!.value)
        assertEquals(500L, normalized.lyricsOffsets["song.flac"]!!.value)
        assertEquals(2, state.tagOverrides.size)
    }

    private val doc = SharedCases.load("sync-merge.json")

    @Test
    fun sharedCases() {
        for (c in doc.cases()) {
            val local = SyncFormat.stateFromJson(c.getJSONObject("local"))
            val remotes = c.getJSONArray("remotes").objects().map { requireNotNull(SyncFormat.parse(it.toString())) { c.description() } }
            val result = SyncMerge.merge(local, remotes, c.getString("self"), c.getLong("now"))
            val expected = c.getJSONObject("expected")
            assertEquals(c.description(), SyncFormat.stateFromJson(expected.getJSONObject("state")), result.state)
            assertEquals(
                c.description(),
                expected.getJSONArray("changes").strings(),
                result.changes.map { "${it.kind}:${it.key}@${it.deviceId}" }.sorted(),
            )
            assertEquals(
                c.description(),
                expected.getJSONArray("replaced").strings(),
                result.replaced.map { (if (it.deleted) "deleted:" else "") + it.old.uid }.sorted(),
            )
        }
    }

    @Test
    fun legacyUids() {
        for (u in doc.getJSONArray("legacy_uids").objects()) {
            assertEquals(u.getString("name"), u.getString("uid"), SyncFormat.legacyUid(u.getString("name")))
        }
    }

    /** 同步文件写出再读回来应该完全一样。 */
    @Test
    fun roundTrip() {
        for (c in doc.cases()) {
            for (remote in c.getJSONArray("remotes").objects()) {
                val file = SyncFormat.parse(remote.toString())!!
                assertEquals(c.description(), file, SyncFormat.parse(SyncFormat.toJson(file).toString()))
            }
        }
    }

    @Test
    fun legacyBackup() {
        val text = JSONObject()
            .put("format", 1)
            .put("app_version", "1.0.70")
            .put("exported_at", "2026-10-16T21:00:00+08:00")
            .put(
                "settings",
                JSONObject().put("artist_separators", listOf("/", ";")).put("artist_exceptions", listOf("AC/DC"))
                    .put("infer_from_file_name", true).put("file_name_format", "ARTIST_TITLE").put("legacy_charset", JSONObject.NULL)
                    .put("theme", "DARK").put("low_power_mode", "BACKGROUND"),
            )
            .put("pinned_tabs", listOf(JSONObject().put("type", "FOLDERS").put("ref", ""), JSONObject().put("type", "ARTIST").put("ref", "周深")))
            .put("playlists", listOf(JSONObject().put("name", "跑步").put("items", listOf("a/b.mp3", "c.flac#02"))))
            .toString()
        val file = SyncFormat.parseLegacy(text)!!
        assertEquals(SyncFormat.LEGACY_DEVICE_ID, file.device.id)
        val p = file.state.playlists.values.single()
        assertEquals(SyncFormat.legacyUid("跑步"), p.uid)
        assertEquals(listOf("a/b.mp3", "c.flac#02"), p.items)
        assertEquals(listOf("/", ";"), file.state.rules!!.value.artistSeparators)
        // 主题、低功耗播放不再同步
        assertEquals(SyncFormat.LEGACY_STAMP, file.state.rules!!.updatedAt)
        assertEquals("周深", file.state.nav!!.value[1].ref)
        assertNull(SyncFormat.parseLegacy("""{"format":2}"""))
    }
}
