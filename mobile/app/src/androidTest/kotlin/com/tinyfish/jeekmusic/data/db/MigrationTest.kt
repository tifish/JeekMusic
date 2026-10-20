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

    private companion object {
        const val DB = "migration-test.db"
    }
}
