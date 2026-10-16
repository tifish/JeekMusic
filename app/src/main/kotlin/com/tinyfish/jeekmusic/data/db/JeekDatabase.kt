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
    ],
    version = 1,
    exportSchema = true,
)
abstract class JeekDatabase : RoomDatabase() {
    abstract fun folderDao(): FolderDao
    abstract fun trackDao(): TrackDao
    abstract fun artistDao(): ArtistDao
}

@Module
@InstallIn(SingletonComponent::class)
object DatabaseModule {

    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): JeekDatabase =
        Room.databaseBuilder(context, JeekDatabase::class.java, "jeek.db")
            // 1.0 发布前数据库结构还会频繁变化，直接重建；发布后改为正式迁移（docs/design.md §3）
            .fallbackToDestructiveMigration(dropAllTables = true)
            .build()

    @Provides
    fun folderDao(db: JeekDatabase) = db.folderDao()

    @Provides
    fun trackDao(db: JeekDatabase) = db.trackDao()

    @Provides
    fun artistDao(db: JeekDatabase) = db.artistDao()
}
