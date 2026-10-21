package com.tinyfish.jeekmusic.data.db

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/**
 * 4 → 5（docs/design.md §6.5）：智能标签从只有"运动 / 安静"扩展为三个维度（氛围、人声、曲风）。
 * - track_features 增加 YAMNet 模型的几组得分（旧数据为 null，分析器版本提高后会重新分析）
 * - track_mood / mood_override 换成按维度存的 track_tag / tag_override，原有结果和手动修正都迁移过去
 */
val MIGRATION_4_5 = object : Migration(4, 5) {
    override fun migrate(db: SupportSQLiteDatabase) {
        for (column in listOf("vocal_score", "classical_score", "calm_level", "energy_level", "orchestra_level")) {
            db.execSQL("ALTER TABLE `track_features` ADD COLUMN `$column` REAL")
        }

        db.execSQL(
            "CREATE TABLE IF NOT EXISTS `tag_override` (`track_key` TEXT NOT NULL, `dimension` TEXT NOT NULL, `value` TEXT, " +
                "PRIMARY KEY(`track_key`, `dimension`))",
        )
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS `track_tag` (`track_key` TEXT NOT NULL, `dimension` TEXT NOT NULL, `value` TEXT NOT NULL, " +
                "`source` TEXT NOT NULL, PRIMARY KEY(`track_key`, `dimension`))",
        )
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_track_tag_dimension_value` ON `track_tag` (`dimension`, `value`)")

        db.execSQL("INSERT INTO `tag_override` (`track_key`, `dimension`, `value`) SELECT `track_key`, 'MOOD', `mood` FROM `mood_override`")
        db.execSQL("INSERT INTO `track_tag` (`track_key`, `dimension`, `value`, `source`) SELECT `track_key`, 'MOOD', `mood`, `source` FROM `track_mood`")
        db.execSQL("DROP TABLE `mood_override`")
        db.execSQL("DROP TABLE `track_mood`")
    }
}

/** Sync timestamps, identity and history commit atomically with playlists and corrections. */
val MIGRATION_5_6 = object : Migration(5, 6) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS `sync_journal` (`id` INTEGER NOT NULL, `content` TEXT NOT NULL, PRIMARY KEY(`id`))")
    }
}
