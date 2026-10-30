package com.tinyfish.jeekmusic.data.db

import androidx.room.testing.MigrationTestHelper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** 数据库迁移（docs/design.md §3）：用 app/schemas/ 中导出的旧版本结构建库，迁移后由 Room 校验结构。 */
@RunWith(AndroidJUnit4::class)
class MigrationTest {

    @get:Rule
    val helper = MigrationTestHelper(InstrumentationRegistry.getInstrumentation(), JeekDatabase::class.java)

    @Test
    fun migrate4To5KeepsMoodsAndOverrides() {
        helper.createDatabase(DB, 4).use { db ->
            db.execSQL(
                "INSERT INTO track_features (track_key, signature, analyzer_version, bpm, bpm_confidence, loudness, loudness_range, " +
                    "onset_rate, centroid_hz, low_ratio, analyzed_at, failed) VALUES ('a', 's', 2, 120, 7, -8, 5, 3.5, 1400, 0.5, 0, 0)",
            )
            db.execSQL("INSERT INTO track_mood (track_key, mood, source) VALUES ('a', 'SPORT', 'AUTO'), ('b', 'QUIET', 'MANUAL')")
            db.execSQL("INSERT INTO mood_override (track_key, mood) VALUES ('b', 'QUIET'), ('c', NULL)")
        }

        helper.runMigrationsAndValidate(DB, 5, true, MIGRATION_4_5).use { db ->
            db.query("SELECT track_key, dimension, value, source FROM track_tag ORDER BY track_key").use { c ->
                val rows = buildList { while (c.moveToNext()) add(listOf(c.getString(0), c.getString(1), c.getString(2), c.getString(3))) }
                assertEquals(listOf(listOf("a", "MOOD", "SPORT", "AUTO"), listOf("b", "MOOD", "QUIET", "MANUAL")), rows)
            }
            db.query("SELECT track_key, dimension, value FROM tag_override ORDER BY track_key").use { c ->
                c.moveToNext()
                assertEquals(listOf("b", "MOOD", "QUIET"), listOf(c.getString(0), c.getString(1), c.getString(2)))
                c.moveToNext()
                assertEquals("c", c.getString(0))
                assertNull(c.getString(2))
            }
            // 旧的分析结果保留，模型得分为空，等待重新分析
            db.query("SELECT analyzer_version, vocal_score FROM track_features").use { c ->
                c.moveToNext()
                assertEquals(2, c.getInt(0))
                assertEquals(true, c.isNull(1))
            }
        }
    }

    @Test
    fun migrate5To6PreservesUserDataAndAddsTransactionalSyncJournal() {
        helper.createDatabase(DB, 5).use { db ->
            db.execSQL("INSERT INTO playlist (id, name, created_at, updated_at) VALUES (1, 'Road trip', 10, 20)")
            db.execSQL("INSERT INTO playlist_item (playlist_id, position, track_key) VALUES (1, 0, 'local:song'), (1, 1, 'foreign:PC only.flac')")
            db.execSQL("INSERT INTO pinned_tab (type, ref, position) VALUES ('ARTIST', 'Test Artist', 0)")
            db.execSQL("INSERT INTO lyrics_offset (track_key, offset_ms) VALUES ('local:song', 250)")
            db.execSQL("INSERT INTO tag_override (track_key, dimension, value) VALUES ('local:song', 'MOOD', 'SPORT')")
            db.execSQL("INSERT INTO queue_item (position, track_key) VALUES (0, 'local:song')")
        }
        helper.runMigrationsAndValidate(DB, 6, true, MIGRATION_5_6).use { db ->
            fun scalar(query: String) = db.query(query).use { c -> check(c.moveToFirst()); c.getString(0) }
            assertEquals("Road trip", scalar("SELECT name FROM playlist WHERE id = 1"))
            assertEquals("20", scalar("SELECT updated_at FROM playlist WHERE id = 1"))
            assertEquals("local:song|foreign:PC only.flac", scalar("SELECT group_concat(track_key, '|') FROM (SELECT track_key FROM playlist_item ORDER BY position)"))
            assertEquals("Test Artist", scalar("SELECT ref FROM pinned_tab"))
            assertEquals("250", scalar("SELECT offset_ms FROM lyrics_offset"))
            assertEquals("SPORT", scalar("SELECT value FROM tag_override"))
            assertEquals("local:song", scalar("SELECT track_key FROM queue_item"))
            assertEquals("0", scalar("SELECT count(*) FROM sync_journal"))
            // A failed merge must roll back user data and its synchronization metadata together.
            db.beginTransaction()
            try {
                db.execSQL("UPDATE playlist SET name = 'Interrupted' WHERE id = 1")
                db.execSQL("INSERT INTO sync_journal (id, content) VALUES (1, '{}')")
            } finally { db.endTransaction() }
            assertEquals("Road trip", scalar("SELECT name FROM playlist WHERE id = 1"))
            assertEquals("0", scalar("SELECT count(*) FROM sync_journal"))
        }
    }

    @Test
    fun migrate6To7PreservesOverridesAndCreatesEmptyEvidence() {
        helper.createDatabase(DB, 6).use { db ->
            db.execSQL("INSERT INTO tag_override (track_key, dimension, value) VALUES ('song', 'MOOD', 'SPORT')")
            db.execSQL("INSERT INTO track_features (track_key, signature, analyzer_version, bpm_confidence, loudness, loudness_range, onset_rate, centroid_hz, low_ratio, analyzed_at, failed) VALUES ('song', 's', 4, 0, -20, 0, 0, 0, 0, 0, 0)")
        }
        helper.runMigrationsAndValidate(DB, 7, true, MIGRATION_6_7).use { db ->
            db.query("SELECT analysis_evidence, analysis_attempts, analyzer_version FROM track_features").use { c ->
                c.moveToFirst(); assertNull(c.getString(0)); assertEquals(0, c.getInt(1)); assertEquals(4, c.getInt(2))
            }
            db.query("SELECT value FROM tag_override").use { c -> c.moveToFirst(); assertEquals("SPORT", c.getString(0)) }
            db.execSQL("INSERT INTO tag_review (track_key, record) VALUES ('song', '{}')")
        }
    }

    private companion object {
        const val DB = "migration-test.db"
    }
}
