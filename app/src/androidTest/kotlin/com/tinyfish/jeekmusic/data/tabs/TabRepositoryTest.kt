package com.tinyfish.jeekmusic.data.tabs

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.PinnedTabEntity
import com.tinyfish.jeekmusic.ui.navigation.Tab
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class TabRepositoryTest {

    private lateinit var db: JeekDatabase
    private lateinit var repository: TabRepository

    @Before
    fun setUp() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
        repository = TabRepository(db, db.tabDao(), db.artistDao())
    }

    @After
    fun tearDown() = db.close()

    private suspend fun keys() = repository.tabs.first().map { it.key }

    @Test
    fun pinnedArtistGoesRightAfterFolders() = runTest {
        keys() // 写入默认标签
        repository.pinArtist("a")
        repository.pinArtist("b")
        repository.pinArtist("b") // 重复固定不产生第二个标签
        assertEquals(
            listOf(Tab.Folders.key, Tab.PinnedArtist("b", "").key, Tab.PinnedArtist("a", "").key, Tab.Artists.key),
            keys().take(4),
        )
        assertEquals(5, keys().size)
    }

    @Test
    fun pinnedArtistFollowsMovedFolders() = runTest {
        keys()
        repository.move(0, 2) // 把"文件夹"移到最后
        repository.pinArtist("a")
        assertEquals(listOf(Tab.Folders.key, Tab.PinnedArtist("a", "").key), keys().takeLast(2))
    }

    @Test
    fun restoredArtistsGoAfterFoldersInBackupOrder() = runTest {
        keys()
        repository.pinArtist("a")
        // 从备份恢复：已经固定的 a 跳过，x、y 按备份里的顺序排在"文件夹"后面
        assertEquals(2, repository.pinArtists(listOf("x", "a", "y")))
        assertEquals(
            listOf(Tab.Folders.key, "ARTIST:x", "ARTIST:y", "ARTIST:a", Tab.Artists.key, Tab.Playlists.key),
            keys(),
        )
    }

    @Test
    fun legacyMoodTabsAreRemoved() = runTest {
        // 旧版本把"运动 / 安静"固定成了标签；现在改成筛选，启动时清掉
        db.tabDao().insert(
            listOf(
                PinnedTabEntity(type = "FOLDERS", position = 0),
                PinnedTabEntity(type = "MOOD", ref = "SPORT", position = 1),
                PinnedTabEntity(type = "MOOD", ref = "QUIET", position = 2),
            ),
        )
        assertEquals(listOf(Tab.Folders.key), keys())
    }
}
