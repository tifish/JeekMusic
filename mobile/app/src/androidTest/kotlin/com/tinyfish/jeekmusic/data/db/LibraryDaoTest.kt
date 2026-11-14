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
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LibraryDaoTest {

    private lateinit var db: JeekDatabase
    private val folders get() = db.folderDao()
    private val tracks get() = db.trackDao()
    private val artists get() = db.artistDao()

    @Test fun vocalLanguagesPersistOverrideInvalidateAndExchangeWithoutResurrectingCorrections() = runTest {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val settings = com.tinyfish.jeekmusic.data.settings.SettingsRepository(context)
        val original = settings.current().tagFilter
        val folder = addFolder("LanguageFixture/")
        val songs = listOf(track(folder,"LanguageFixture/","English title.mp3"), track(folder,"LanguageFixture/","Chinese title.mp3"))
        val ids = tracks.insert(songs)
        val targets = songs.zip(ids).map { (s,id) -> s.copy(id=id) }
        val repo = com.tinyfish.jeekmusic.data.filter.LanguageRepository(db)
        val filters = com.tinyfish.jeekmusic.data.filter.TagFilterRepository(settings,db.tagDao(),repo)
        try {
            repo.save(targets[0],com.tinyfish.jeekmusic.analysis.SingingLanguage("known",listOf("zh"),confidence=.95))
            repo.save(targets[1],com.tinyfish.jeekmusic.analysis.SingingLanguage("unknown",emptyList()))
            settings.setTagFilter(com.tinyfish.jeekmusic.data.settings.TagFilter(chinese=com.tinyfish.jeekmusic.analysis.LanguageFilter.NON_CHINESE))
            assertTrue(filters.allowedKeys.first()!!.isEmpty())
            settings.setTagFilter(com.tinyfish.jeekmusic.data.settings.TagFilter(chinese=com.tinyfish.jeekmusic.analysis.LanguageFilter.CHINESE))
            assertEquals(setOf(targets[0].trackKey),filters.allowedKeys.first())
            repo.setManual(targets,"ENGLISH")
            assertTrue(filters.allowedKeys.first()!!.isEmpty())
            val exchange=com.tinyfish.jeekmusic.data.sync.LanguageExchange(db)
            val snapshot=exchange.export { it }
            repo.setManual(targets,"AUTO")
            assertEquals("AUTO",repo.result(targets[0])!!.source)
            exchange.accept(snapshot) { it }
            assertEquals("AUTO",repo.result(targets[0])!!.source) // older manual result cannot resurrect
            val row=exchange.export { it }.getJSONObject("items").getJSONArray(targets[0].trackKey).getJSONObject(0)
            assertTrue(row.has("updated_at"))
            tracks.update(listOf(targets[0].copy(size=1)))
            assertTrue(repo.results.first().keys.none { it==targets[0].trackKey })
            tracks.delete(ids)
            assertTrue(db.languageDao().all().isEmpty())
        } finally { settings.setTagFilter(original) }
    }

    @Test fun batchTagCorrectionCommitsTogetherAndRollsBackOnAnyFailure() = runTest {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val settings = com.tinyfish.jeekmusic.data.settings.SettingsRepository(context)
        val folder=addFolder("BatchFixture/")
        val songs=(1..3).map { track(folder,"BatchFixture/","$it.wav") }
        val ids=tracks.insert(songs); val targets=songs.zip(ids).map { (s,id)->s.copy(id=id) }
        val feedback=com.tinyfish.jeekmusic.data.preference.DailyLearningFeedback(object: android.content.ContextWrapper(context) {
            override fun getNoBackupFilesDir()=java.io.File(context.cacheDir,"parity-batch-"+System.nanoTime()).apply { mkdirs() }
        },db)
        val repository=com.tinyfish.jeekmusic.analysis.TagRepository(db,db.tagDao(),settings,feedback)
        try {
        val voice=com.tinyfish.jeekmusic.analysis.TagDimension.VOICE
        val mood=com.tinyfish.jeekmusic.analysis.TagDimension.MOOD
        repository.applyManualBatch(targets,mapOf(voice to com.tinyfish.jeekmusic.analysis.Tag.VOCAL))
        assertEquals(3,db.tagDao().overrides().size)
        db.openHelper.writableDatabase.execSQL("CREATE TRIGGER fail_batch BEFORE INSERT ON tag_override WHEN NEW.track_key='${targets.last().trackKey}' AND NEW.dimension='MOOD' BEGIN SELECT RAISE(ABORT,'fixture'); END")
        try { repository.applyManualBatch(targets,mapOf(mood to com.tinyfish.jeekmusic.analysis.Tag.SPORT)); error("expected rollback") }
        catch (_: android.database.sqlite.SQLiteException) { }
        assertTrue(db.tagDao().overrides().none { it.dimension=="MOOD" })
        assertEquals(3,db.tagDao().overrides().count { it.dimension=="VOICE" })
        repository.applyManualBatch(targets,mapOf(voice to null))
        assertTrue(targets.all { voice !in repository.info(it.trackKey).manual })
        } finally { feedback.close() }
    }

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
    fun partialAnalysisRetriesOnceUntilTerminalAndStaleSignaturesAreNotDisplayed() = runTest {
        val folder = addFolder("Music/")
        val track = track(folder, "Music/", "controlled.wav")
        tracks.insert(listOf(track))
        val dao = db.tagDao()
        val partial = TrackFeaturesEntity(track.trackKey, "0:0", 5, null, 0.0, -20.0, 0.0, 0.0, 0.0, 0.0, 0,
            failed = false, vocalScore = 0.0, analysisEvidence = "{}", analysisAttempts = 1)
        dao.saveFeatures(partial)
        assertEquals(1, dao.pending(5, 10).size)
        dao.saveFeatures(partial.copy(failed = true, analysisAttempts = 3))
        assertEquals(0, dao.pending(5, 10).size)
        assertEquals(0, dao.observePendingCount(5).first())
        dao.saveFeatures(partial.copy(failed = true, analysisAttempts = 3, analyzerVersion = 4))
        assertEquals(1, dao.pending(5, 10).size)
        dao.saveFeatures(partial.copy(signature = "stale", analysisAttempts = 3))
        assertEquals(1, dao.pending(5, 10).size)
        assertEquals(0, dao.allFeatures().size)
        assertEquals(null, dao.currentFeatures(track.trackKey))
    }

    @Test
    fun calmnessCacheManualAutoAndRollback() = runTest {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val settings = com.tinyfish.jeekmusic.data.settings.SettingsRepository(context)
        val previous = settings.current()
        val repository = com.tinyfish.jeekmusic.analysis.TagRepository(db, db.tagDao(), settings, com.tinyfish.jeekmusic.data.preference.DailyLearningFeedback(object: android.content.ContextWrapper(context) { override fun getNoBackupFilesDir()=java.io.File(context.cacheDir,"daily-library-test-"+System.nanoTime()).apply { mkdirs() } },db))
        val folder = addFolder("Calmness/")
        val track = track(folder, "Calmness/", "Sonata Op. 1.mp3")
        tracks.insert(listOf(track))
        val m = com.tinyfish.jeekmusic.analysis.ModelScores(-3.0, -1.0, .015, .005, 0.0)
        val e = com.tinyfish.jeekmusic.analysis.AnalysisEvidence(listOf(
            com.tinyfish.jeekmusic.analysis.SegmentEvidence(0, 9750, false, 156000, 1, 1, m, List(10) { m }, null)), emptyList(), true, null, 1)
        val f = TrackFeaturesEntity(track.trackKey, "0:0", 5, null, 0.0, -20.0, 0.0, 0.0, 0.0, 0.0, 0,
            vocalScore = m.vocal, classicalScore = m.classical, calmLevel = m.calm, energyLevel = m.energy,
            orchestraLevel = m.orchestra, analysisEvidence = e.toJson())
        val dao = db.tagDao()
        dao.saveFeatures(f)
        dao.setOverride(TagOverrideEntity(track.trackKey, "MOOD", "QUIET"))
        val dim = com.tinyfish.jeekmusic.analysis.TagDimension.CALMNESS
        val calm = com.tinyfish.jeekmusic.analysis.Tag.CALM
        try {
            settings.setTagRulesVersion(6)
            repository.recomputeIfRulesChanged()
            assertEquals(com.tinyfish.jeekmusic.analysis.TagRepository.RULES_VERSION, settings.current().tagRulesVersion)
            val migrated = dao.currentFeatures(track.trackKey)!!
            assertEquals(6,migrated.analyzerVersion)
            assertEquals(f.copy(analyzerVersion=6,analysisEvidence=migrated.analysisEvidence),migrated)
            val migratedEvidence = com.tinyfish.jeekmusic.analysis.AnalysisEvidence.parse(migrated.analysisEvidence)!!
            assertTrue(migratedEvidence.baseEvidenceReused)
            assertEquals("not_needed",migratedEvidence.rhythmState)
            assertEquals(e.aggregate(),migratedEvidence.aggregate())
            assertEquals(0, dao.pending(6, 10).size)
            assertEquals(calm, repository.info(track.trackKey).tags[dim])
            assertEquals(com.tinyfish.jeekmusic.analysis.Tag.NON_SPORT, repository.info(track.trackKey).tags[com.tinyfish.jeekmusic.analysis.TagDimension.MOOD])
            repository.setManual(track.trackKey, dim, com.tinyfish.jeekmusic.analysis.Tag.NON_CALM)
            assertEquals(setOf(track.trackKey), dao.observeKeys("CALMNESS", "NON_CALM").first().toSet())
            val reviews = dao.reviews(track.trackKey).size
            db.openHelper.writableDatabase.execSQL("CREATE TRIGGER fail_calm BEFORE INSERT ON track_tag WHEN NEW.dimension='CALMNESS' BEGIN SELECT RAISE(ABORT,'fixture'); END")
            var failed = false
            try { repository.clearManual(track.trackKey, dim) } catch (_: android.database.sqlite.SQLiteException) { failed = true }
            assertEquals(true, failed)
            assertEquals(reviews, dao.reviews(track.trackKey).size)
            assertEquals(com.tinyfish.jeekmusic.analysis.Tag.NON_CALM, repository.info(track.trackKey).tags[dim])
            db.openHelper.writableDatabase.execSQL("DROP TRIGGER fail_calm")
            repository.clearManual(track.trackKey, dim)
            assertEquals(calm, repository.info(track.trackKey).tags[dim])
            assertEquals(false, dim in repository.info(track.trackKey).manual)
            assertEquals("QUIET", dao.overrides(track.trackKey).single().value)
            assertEquals(migrated, dao.currentFeatures(track.trackKey))
            val filter = com.tinyfish.jeekmusic.data.settings.TagFilter(mood = com.tinyfish.jeekmusic.analysis.Tag.NON_SPORT, calmness = calm)
            settings.setTagFilter(filter)
            assertEquals(filter, com.tinyfish.jeekmusic.data.settings.SettingsRepository(context).current().tagFilter)
            settings.setTagFilter(com.tinyfish.jeekmusic.data.settings.TagFilter())
            assertEquals(false, settings.current().tagFilter.isActive)
            dao.saveFeatures(migrated.copy(failed = true, analysisAttempts = 3))
            repository.recomputeAll()
            assertEquals(null, repository.info(track.trackKey).tags[dim])
            assertEquals(0, dao.pending(6, 10).size)
        } finally {
            settings.setTagFilter(previous.tagFilter)
            settings.setTagRulesVersion(previous.tagRulesVersion)
        }
    }

    @Test
    fun unannotatedIsCurrentManualComplementAcrossFullLibraryAndCounts() = runTest {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val settings = com.tinyfish.jeekmusic.data.settings.SettingsRepository(context)
        val previous = settings.current().tagFilter
        val folder = addFolder("Annotation/")
        val items = (1..245).map { track(folder, "Annotation/", "song$it.mp3") }
        val ids = tracks.insert(items)
        val dao = db.tagDao()
        artists.insert(listOf(ArtistEntity(name="Fixture",nameKey="fixture",sortKey="fixture",indexLetter="F")))
        val artistId=artists.byNameKeys(listOf("fixture")).single().id
        artists.insertLinks(ids.map { TrackArtistEntity(it,artistId) })
        val feedback=com.tinyfish.jeekmusic.data.preference.DailyLearningFeedback(object: android.content.ContextWrapper(context) {
            override fun getNoBackupFilesDir()=java.io.File(context.cacheDir,"annotation-test-"+System.nanoTime()).apply { mkdirs() }
        },db)
        val repository=com.tinyfish.jeekmusic.analysis.TagRepository(db,dao,settings,feedback)
        val filters=com.tinyfish.jeekmusic.data.filter.TagFilterRepository(settings,dao)
        var feedbackClosed=false
        try {
            settings.setTagFilter(com.tinyfish.jeekmusic.data.settings.TagFilter(onlyUnannotated=true))
            assertEquals(245,filters.allowedKeys.first()!!.size) // No model analysis at all.
            assertEquals(245,folders.observeWithCounts(null,null,null,onlyUnannotated=true).first().single().trackCount)
            for (d in com.tinyfish.jeekmusic.analysis.TagDimension.entries) {
                val value=com.tinyfish.jeekmusic.analysis.Tag.of(d).first()
                repository.setManual(items.last().trackKey,d,value)
                assertEquals(244,filters.allowedKeys.first()!!.size)
                assertEquals(listOf(items.last().trackKey),dao.observeManualKeys().first())
                assertEquals(244,artists.observeCounts(null,null,null,onlyUnannotated=true).first().single().count)
                assertEquals(244,tracks.observeDirectoryCounts(folder,"Annotation/",null,null,null,onlyUnannotated=true).first().sumOf { it.count })
                repository.clearManual(items.last().trackKey,d)
                assertEquals(245,filters.allowedKeys.first()!!.size) // Audit still exists.
            }
            feedback.close(); feedbackClosed=true // Drain background recomputation before direct result fixtures.
            // Same value with AUTO and MANUAL sources; PERSONAL is also unannotated.
            dao.insertTags(listOf(TrackTagEntity(items[0].trackKey,"VOICE","VOCAL","AUTO"),
                TrackTagEntity(items[1].trackKey,"VOICE","VOCAL","MANUAL"),
                TrackTagEntity(items[2].trackKey,"VOICE","VOCAL","PERSONAL")))
            settings.setTagFilter(com.tinyfish.jeekmusic.data.settings.TagFilter(voice=com.tinyfish.jeekmusic.analysis.Tag.VOCAL,onlyUnannotated=true))
            assertEquals(setOf(items[0].trackKey,items[2].trackKey),filters.allowedKeys.first())
            assertEquals(2,folders.observeWithCounts(null,"VOCAL",null,onlyUnannotated=true).first().single().trackCount)
            val saved=com.tinyfish.jeekmusic.data.settings.SettingsRepository(context).current().tagFilter
            assertEquals(true,saved.unannotated)
            settings.setTagFilter(saved.copy(onlyManual=true)) // Legacy true wins malformed dual input.
            assertEquals(false,settings.current().tagFilter.onlyUnannotated)
            assertEquals(setOf(items[1].trackKey),filters.allowedKeys.first())
        } finally { if(!feedbackClosed) feedback.close(); settings.setTagFilter(previous) }
    }

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
        val counts = tracks.observeDirectoryCounts(id, "Music/", null, null, null).first()
        assertEquals(
            listOf(FolderNode("100%_Hits", "Music/100%_Hits/", 1), FolderNode("Pop", "Music/Pop/", 2)),
            FolderTree.children("Music/", counts),
        )
        // LIKE 中的 % 和 _ 必须被转义，否则 "Music/100%_Hits/" 会匹配到别的目录
        val escaped = tracks.observeDirectoryCounts(id, "Music/100\\%\\_Hits/", null, null, null).first()
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
        assertEquals(listOf("100%_Pure.mp3"), search.tracks("%100\\%\\_%").map { it.fileName })
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
        assertEquals(listOf(1, 2), folders.observeWithCounts(null, null, null).first().sortedBy { it.trackCount }.map { it.trackCount })

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

    @Test
    fun tagFilterCounts() = runTest {
        val id = addFolder("Music/")
        val ids = tracks.insert(
            listOf(track(id, "Music/", "a.mp3"), track(id, "Music/Pop/", "b.mp3"), track(id, "Music/Rock/", "c.mp3")),
        )
        val tags = db.tagDao()
        tags.insertTags(
            listOf(
                TrackTagEntity("external_primary:Music/a.mp3", "MOOD", "SPORT", "AUTO"),
                TrackTagEntity("external_primary:Music/a.mp3", "VOICE", "VOCAL", "AUTO"),
                TrackTagEntity("external_primary:Music/a.mp3", "CALMNESS", "NON_CALM", "AUTO"),
                TrackTagEntity("external_primary:Music/Pop/b.mp3", "MOOD", "SPORT", "MANUAL"),
                TrackTagEntity("external_primary:Music/Pop/b.mp3", "VOICE", "INSTRUMENTAL", "AUTO"),
                TrackTagEntity("external_primary:Music/Pop/b.mp3", "CALMNESS", "CALM", "MANUAL"),
                TrackTagEntity("external_primary:Music/Rock/c.mp3", "MOOD", "QUIET", "AUTO"),
                TrackTagEntity("external_primary:Music/Rock/c.mp3", "VOICE", "VOCAL", "AUTO"),
            ),
        )
        // 筛选"运动"：Rock 子文件夹里没有运动歌曲，不再出现
        assertEquals(
            listOf(FolderNode("Pop", "Music/Pop/", 1)),
            FolderTree.children("Music/", tracks.observeDirectoryCounts(id, "Music/", "SPORT", null, null).first()),
        )
        assertEquals(listOf(2), folders.observeWithCounts("SPORT", null, null).first().map { it.trackCount })
        // 两个维度同时筛选："运动"且"人声"只有 a
        assertEquals(listOf(1), folders.observeWithCounts("SPORT", "VOCAL", null).first().map { it.trackCount })
        assertEquals(listOf(0), folders.observeWithCounts("SPORT", null, "CLASSICAL").first().map { it.trackCount })
        assertEquals(setOf("external_primary:Music/Rock/c.mp3"), tags.observeKeys("MOOD", "QUIET").first().toSet())

        artists.insert(listOf(ArtistEntity(name = "A", nameKey = "a", sortKey = "a", indexLetter = "A")))
        val artistId = artists.byNameKeys(listOf("a")).single().id
        artists.insertLinks(ids.map { TrackArtistEntity(it, artistId) })
        assertEquals(listOf(ArtistCount(artistId, 2)), artists.observeCounts("SPORT", null, null).first())
        assertEquals(listOf(ArtistCount(artistId, 2)), artists.observeCounts(null, "VOCAL", null).first())
        assertEquals(emptyList<ArtistCount>(), artists.observeCounts("QUIET", "INSTRUMENTAL", null).first())
        assertEquals(listOf(1), folders.observeWithCounts(null, null, null, "CALM").first().map { it.trackCount })
        assertEquals(listOf(0), folders.observeWithCounts("SPORT", "VOCAL", null, "CALM").first().map { it.trackCount })
        assertEquals(listOf(ArtistCount(artistId, 1)), artists.observeCounts("SPORT", "INSTRUMENTAL", null, "CALM").first())
        assertEquals(1, tracks.observeDirectoryCounts(id, "Music/", null, null, null, "NON_CALM").first().sumOf { it.count })
        assertEquals(listOf(3), folders.observeWithCounts(null, null, null, null).first().map { it.trackCount })
    }
}
