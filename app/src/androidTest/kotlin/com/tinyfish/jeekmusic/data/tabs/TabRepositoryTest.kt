package com.tinyfish.jeekmusic.data.tabs

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.db.JeekDatabase
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
        assertEquals(7, keys().size)
    }

    @Test
    fun pinnedArtistFollowsMovedFolders() = runTest {
        keys()
        repository.move(0, 4) // 把"文件夹"移到最后
        repository.pinArtist("a")
        assertEquals(listOf(Tab.Folders.key, Tab.PinnedArtist("a", "").key), keys().takeLast(2))
    }
}
