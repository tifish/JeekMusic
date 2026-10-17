package com.tinyfish.jeekmusic.data.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Database(
    entities = [
        LibraryFolderEntity::class,
        TrackEntity::class,
        ArtistEntity::class,
        TrackArtistEntity::class,
        PinnedTabEntity::class,
        PlaylistEntity::class,
        PlaylistItemEntity::class,
        QueueItemEntity::class,
        LyricsOffsetEntity::class,
        LyricsCacheEntity::class,
        TrackFeaturesEntity::class,
        TagOverrideEntity::class,
        TrackTagEntity::class,
    ],
    version = 5,
    exportSchema = true,
)
abstract class JeekDatabase : RoomDatabase() {
    abstract fun folderDao(): FolderDao
    abstract fun trackDao(): TrackDao
    abstract fun artistDao(): ArtistDao
    abstract fun tabDao(): TabDao
    abstract fun playlistDao(): PlaylistDao
    abstract fun queueDao(): QueueDao
    abstract fun searchDao(): SearchDao
    abstract fun lyricsDao(): LyricsDao
    abstract fun tagDao(): TagDao
}

@Module
@InstallIn(SingletonComponent::class)
object DatabaseModule {

    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): JeekDatabase =
        Room.databaseBuilder(context, JeekDatabase::class.java, "jeek.db")
            // 1.0 起数据库结构变化必须提供 Migration（app/schemas/ 中导出了每个版本的结构），
            // 不再直接重建，以免丢失播放列表等用户数据；只有降级时才重建（docs/design.md §3）
            .addMigrations(MIGRATION_4_5)
            .fallbackToDestructiveMigrationOnDowngrade(dropAllTables = true)
            .build()

    @Provides
    fun folderDao(db: JeekDatabase) = db.folderDao()

    @Provides
    fun trackDao(db: JeekDatabase) = db.trackDao()

    @Provides
    fun artistDao(db: JeekDatabase) = db.artistDao()

    @Provides
    fun tabDao(db: JeekDatabase) = db.tabDao()

    @Provides
    fun playlistDao(db: JeekDatabase) = db.playlistDao()

    @Provides
    fun queueDao(db: JeekDatabase) = db.queueDao()

    @Provides
    fun searchDao(db: JeekDatabase) = db.searchDao()

    @Provides
    fun lyricsDao(db: JeekDatabase) = db.lyricsDao()

    @Provides
    fun tagDao(db: JeekDatabase) = db.tagDao()
}
