package com.tinyfish.jeekmusic.data.db

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.data.library.FolderNode
import com.tinyfish.jeekmusic.data.library.FolderTree
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LibraryDaoTest {

    private lateinit var db: JeekDatabase
    private val folders get() = db.folderDao()
    private val tracks get() = db.trackDao()
    private val artists get() = db.artistDao()

    @Before
    fun setUp() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        db = Room.inMemoryDatabaseBuilder(context, JeekDatabase::class.java).build()
    }

    @After
    fun tearDown() = db.close()

    private fun track(folderId: Long, path: String, name: String, album: String? = null) = TrackEntity(
        trackKey = "external_primary:$path$name",
        fileKey = "external_primary:$path$name",
        mediaStoreId = name.hashCode().toLong(),
        volumeName = "external_primary",
        folderId = folderId,
        relativePath = path,
        fileName = name,
        titleTag = null,
        title = name,
        album = album,
        albumArtist = null,
        albumKey = album?.let { "d:$it" },
        artistRaw = null,
        artistDisplay = "",
        artistInferred = false,
        trackNo = null,
        discNo = null,
        year = null,
        genre = null,
        durationMs = 1000,
        mimeType = "audio/mpeg",
        bitrate = null,
        sampleRate = null,
        dateModified = 0,
        size = 0,
        hasEmbeddedLyrics = false,
    )

    private suspend fun addFolder(path: String) = folders.insert(
        LibraryFolderEntity(
            treeUri = "content://tree/$path",
            treeDocumentId = "primary:$path",
            volumeName = "external_primary",
            relativePath = path,
            displayName = path.trimEnd('/').substringAfterLast('/'),
            addedAt = 0,
        ),
    )

    @Test
    fun directoryCountsFoldIntoChildren() = runTest {
        val id = addFolder("Music/")
        tracks.insert(
            listOf(
                track(id, "Music/", "a.mp3"),
                track(id, "Music/Pop/", "b.mp3"),
                track(id, "Music/Pop/2004/", "c.mp3"),
                track(id, "Music/100%_Hits/", "d.mp3"),
            ),
        )
        val counts = tracks.observeDirectoryCounts(id, "Music/").first()
        assertEquals(
            listOf(FolderNode("100%_Hits", "Music/100%_Hits/", 1), FolderNode("Pop", "Music/Pop/", 2)),
            FolderTree.children("Music/", counts),
        )
        // LIKE 中的 % 和 _ 必须被转义，否则 "Music/100%_Hits/" 会匹配到别的目录
        val escaped = tracks.observeDirectoryCounts(id, "Music/100\\%\\_Hits/").first()
        assertEquals(1, escaped.sumOf { it.count })
        assertEquals(1, tracks.observeInDirectory(id, "Music/").first().size)
    }

    @Test
    fun underAndSearchEscapeLikeWildcards() = runTest {
        val id = addFolder("Music/")
        tracks.insert(listOf(track(id, "Music/100%_Hits/", "a.mp3"), track(id, "Music/1000 Hits/", "b.mp3")))
        assertEquals(listOf("a.mp3"), tracks.under(id, "Music/100\\%\\_Hits/").map { it.fileName })

        val search = db.searchDao()
        tracks.insert(listOf(track(id, "Music/", "100%_Pure.mp3"), track(id, "Music/", "1000 Pure.mp3")))
        // "%" 和 "_" 转义后只匹配字面值，不会把 "1000 Pure" 也搜出来
        assertEquals(listOf("100%_Pure.mp3"), search.tracks("%100\\%\\_%", 10).map { it.fileName })
        artists.insert(listOf(ArtistEntity(name = "50% Off", nameKey = "50% off", sortKey = "50% off", indexLetter = "#")))
        assertEquals(listOf("50% Off"), search.artists("%50\\%%", 10).map { it.name })
    }

    @Test
    fun playlistEntriesKeepUnavailableTracks() = runTest {
        val id = addFolder("Music/")
        tracks.insert(listOf(track(id, "Music/", "a.mp3")))
        val playlists = db.playlistDao()
        val pid = playlists.insert(PlaylistEntity(name = "p", createdAt = 0, updatedAt = 0))
        playlists.insertItems(
            listOf(
                PlaylistItemEntity(playlistId = pid, position = 0, trackKey = "external_primary:Music/a.mp3"),
                PlaylistItemEntity(playlistId = pid, position = 1, trackKey = "external_primary:Music/gone.mp3"),
            ),
        )
        val entries = playlists.observeEntries(pid).first()
        assertEquals(listOf("a.mp3", null), entries.map { it.track?.fileName })
    }

    @Test
    fun folderCountsAndCascadeDelete() = runTest {
        val a = addFolder("Music/")
        val b = addFolder("Download/")
        tracks.insert(listOf(track(a, "Music/", "1.mp3"), track(a, "Music/x/", "2.mp3"), track(b, "Download/", "3.mp3")))
        assertEquals(listOf(1, 2), folders.observeWithCounts().first().sortedBy { it.trackCount }.map { it.trackCount })

        folders.delete(listOf(a))
        assertEquals(1, tracks.signatures().size)
    }

    @Test
    fun artistLinksOrphansAndCounts() = runTest {
        val id = addFolder("Music/")
        val ids = tracks.insert(listOf(track(id, "Music/", "1.mp3"), track(id, "Music/", "2.mp3")))
        artists.insert(
            listOf(
                ArtistEntity(name = "周杰伦", nameKey = "周杰伦", sortKey = "zhou jie lun", indexLetter = "Z"),
                ArtistEntity(name = "费玉清", nameKey = "费玉清", sortKey = "fei yu qing", indexLetter = "F"),
            ),
        )
        val byKey = artists.byNameKeys(listOf("周杰伦", "费玉清")).associate { it.nameKey to it.id }
        artists.insertLinks(
            listOf(
                TrackArtistEntity(ids[0], byKey.getValue("周杰伦")),
                TrackArtistEntity(ids[0], byKey.getValue("费玉清")),
                TrackArtistEntity(ids[1], byKey.getValue("周杰伦")),
            ),
        )
        artists.updateTrackCounts()
        assertEquals(listOf("费玉清" to 1, "周杰伦" to 2), artists.observeAll().first().map { it.name to it.trackCount })
        assertEquals(2, tracks.observeByArtist("周杰伦").first().size)

        tracks.delete(listOf(ids[0]))
        artists.deleteOrphans()
        assertEquals(listOf("周杰伦"), artists.observeAll().first().map { it.name })
    }
}
