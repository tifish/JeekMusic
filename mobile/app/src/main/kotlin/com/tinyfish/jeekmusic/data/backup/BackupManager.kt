package com.tinyfish.jeekmusic.data.backup

import com.tinyfish.jeekmusic.data.tabs.TabRepository
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.util.Log
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.tinyfish.jeekmusic.BuildConfig
import com.tinyfish.jeekmusic.data.db.FolderDao
import com.tinyfish.jeekmusic.data.db.JeekDatabase
import com.tinyfish.jeekmusic.data.db.LibraryFolderEntity
import com.tinyfish.jeekmusic.data.db.PlaylistDao
import com.tinyfish.jeekmusic.data.db.TabDao
import com.tinyfish.jeekmusic.data.db.TrackDao
import com.tinyfish.jeekmusic.data.library.DocumentTree
import com.tinyfish.jeekmusic.data.library.create
import com.tinyfish.jeekmusic.data.library.delete
import com.tinyfish.jeekmusic.data.library.findChild
import com.tinyfish.jeekmusic.data.library.rename
import com.tinyfish.jeekmusic.data.library.write
import com.tinyfish.jeekmusic.data.playlist.PlaylistRepository
import com.tinyfish.jeekmusic.data.scanner.ArtistSplitConfig
import com.tinyfish.jeekmusic.data.scanner.FileNameFormat
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.settings.LowPowerMode
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.data.settings.ThemeMode
import com.tinyfish.jeekmusic.di.ApplicationScope
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.nio.charset.Charset
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import javax.inject.Inject
import javax.inject.Singleton

/** 一份解析好的备份。 */
data class Backup(
    val exportedAt: String,
    val settings: JSONObject?,
    val pinnedTabs: List<Pair<String, String>>,
    val playlists: List<Pair<String, List<String>>>,
)

data class RestoreReport(val playlists: Int, val items: Int, val missing: Int, val pinnedTabs: Int)

private val Context.backupStore by preferencesDataStore(name = "backup")

/**
 * 数据备份（需求 B-1 ~ B-6，docs/design.md §3.1）：
 * 播放列表、固定标签、设置导出为 JSON，写入每个音乐文件夹下的 .jeekmusic/backup.json。
 */
@Singleton
class BackupManager @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val db: JeekDatabase,
    private val folderDao: FolderDao,
    private val trackDao: TrackDao,
    private val playlistDao: PlaylistDao,
    private val tabDao: TabDao,
    private val tabs: TabRepository,
    private val playlists: PlaylistRepository,
    private val settings: SettingsRepository,
    private val scanner: LibraryScanner,
    private val tree: DocumentTree,
    @param:ApplicationScope private val scope: CoroutineScope,
) {
    private val mutex = Mutex()
    private var lastContent: String? = null
    private val lastBackupKey = longPreferencesKey("last_backup")

    val lastBackupTime: Flow<Long?> = context.backupStore.data.map { it[lastBackupKey] }

    /**
     * 添加文件夹时发现的备份，等待用户确认（需求 B-3）。
     * 放在单例里，由主界面统一显示对话框：添加文件夹的页面在曲库变为非空后就离开了屏幕。
     */
    private val _offer = MutableStateFlow<Backup?>(null)
    val offer: StateFlow<Backup?> = _offer.asStateFlow()

    suspend fun offerIfFound(folder: LibraryFolderEntity) {
        val backup = withContext(Dispatchers.IO) { find(folder) } ?: return
        if (hasNoUserData()) _offer.value = backup
    }

    fun dismissOffer() {
        _offer.value = null
    }

    /** 等扫描完成后恢复（扫描完成后才能把相对路径映射回歌曲）。 */
    suspend fun restoreOffered(): RestoreReport? {
        val backup = _offer.value ?: return null
        _offer.value = null
        delay(500)
        scanner.busy.first { !it }
        return restore(backup)
    }

    /** 备份范围内的数据变化后防抖 30 秒自动备份（需求 B-1）。 */
    @OptIn(FlowPreview::class)
    fun startAutoBackup() {
        scope.launch {
            merge(
                db.invalidationTracker.createFlow("playlist", "playlist_item", "pinned_tab").drop(1),
                settings.settings.map { it.copy(unsupportedFileCount = 0) }.drop(1),
            ).debounce(30_000).collect { backupNow() }
        }
    }

    /** 立即备份到所有音乐文件夹；内容没有变化时不写文件。返回成功写入的文件夹数。 */
    suspend fun backupNow(force: Boolean = false): Int = mutex.withLock {
        withContext(Dispatchers.IO) {
            val folders = folderDao.all()
            if (folders.isEmpty()) return@withContext 0
            // 刚安装或清除数据后还没有任何用户数据：自动备份不能用空数据覆盖音乐目录里已有的备份，否则用户就没法恢复了。
            // 只有用户手动点"立即备份"时才写。
            if (!force && hasNoUserData()) return@withContext 0
            val content = export(folders)
            if (!force && content.toString() == lastContent) return@withContext folders.size
            lastContent = content.toString()
            content.put("exported_at", DateTimeFormatter.ISO_OFFSET_DATE_TIME.format(Instant.now().atZone(ZoneId.systemDefault())))
            val bytes = content.toString(2).toByteArray(Charsets.UTF_8)
            val written = folders.count { writeTo(it, bytes) }
            if (written > 0) context.backupStore.edit { it[lastBackupKey] = System.currentTimeMillis() }
            Log.i(TAG, "backup written to $written/${folders.size} folders")
            written
        }
    }

    private suspend fun export(folders: List<LibraryFolderEntity>): JSONObject {
        val roots = folders.map { BackupRefs.Root(it.volumeName, it.relativePath) }
        val s = settings.current()
        return JSONObject()
            .put("format", FORMAT)
            .put("app_version", BuildConfig.VERSION_NAME)
            .put(
                "settings",
                JSONObject()
                    .put("artist_separators", JSONArray(s.scan.split.separators.sorted()))
                    .put("artist_exceptions", JSONArray(s.scan.split.exceptions))
                    .put("infer_from_file_name", s.scan.inferFromFileName)
                    .put("file_name_format", s.scan.fileNameFormat.name)
                    .put("legacy_charset", s.scan.legacyCharset?.name() ?: JSONObject.NULL)
                    .put("theme", s.themeMode.name)
                    .put("low_power_mode", s.lowPowerMode.name),
            )
            .put(
                "pinned_tabs",
                JSONArray(tabDao.all().map { JSONObject().put("type", it.type).put("ref", it.ref) }),
            )
            .put(
                "playlists",
                JSONArray(
                    playlistDao.all().map { playlist ->
                        val refs = playlistDao.items(playlist.id).mapNotNull { BackupRefs.refOf(it.trackKey, roots) }
                        JSONObject().put("name", playlist.name).put("items", JSONArray(refs))
                    },
                ),
            )
    }

    /**
     * 原子替换（需求 B-6）：先写 backup.json.tmp，再把旧的 backup.json 改名为 backup.prev.json，最后把 tmp 改名为 backup.json。
     * DocumentsProvider 不支持改名时，退化为直接覆盖写入。
     */
    private fun writeTo(folder: LibraryFolderEntity, bytes: ByteArray): Boolean {
        val root = folder.treeDocumentId
        val dir = tree.findChild(folder, root, DIR)
            ?: tree.create(folder, root, DocumentsContract.Document.MIME_TYPE_DIR, DIR)
            ?: return false
        tree.findChild(folder, dir.documentId, TMP)?.let { tree.delete(folder, it.documentId) }
        val tmp = tree.create(folder, dir.documentId, "application/octet-stream", TMP) ?: return false
        if (tmp.name != TMP || !tree.write(folder, tmp.documentId, bytes)) {
            tree.delete(folder, tmp.documentId)
            return false
        }
        val current = tree.findChild(folder, dir.documentId, FILE)
        if (current != null) {
            tree.findChild(folder, dir.documentId, PREV)?.let { tree.delete(folder, it.documentId) }
            if (!tree.rename(folder, current.documentId, PREV)) {
                // 不支持改名：直接覆盖
                tree.delete(folder, tmp.documentId)
                return tree.write(folder, current.documentId, bytes)
            }
        }
        if (tree.rename(folder, tmp.documentId, FILE)) return true
        tree.delete(folder, tmp.documentId)
        val target = tree.create(folder, dir.documentId, "application/octet-stream", FILE) ?: return false
        return tree.write(folder, target.documentId, bytes)
    }

    /** 读取某个音乐文件夹里的备份；backup.json 损坏时改用 backup.prev.json。 */
    fun find(folder: LibraryFolderEntity): Backup? {
        val dir = tree.findChild(folder, folder.treeDocumentId, DIR) ?: return null
        for (name in listOf(FILE, PREV)) {
            val file = tree.findChild(folder, dir.documentId, name) ?: continue
            tree.read(folder, file.documentId)?.let(::parse)?.let { return it }
        }
        return null
    }

    fun read(uri: Uri): Backup? = runCatching {
        context.contentResolver.openInputStream(uri)?.use { parse(it.readBytes()) }
    }.getOrNull()

    private fun parse(bytes: ByteArray): Backup? = runCatching {
        val json = JSONObject(String(bytes, Charsets.UTF_8))
        if (json.optInt("format") != FORMAT) return null
        Backup(
            exportedAt = json.optString("exported_at"),
            settings = json.optJSONObject("settings"),
            pinnedTabs = json.optJSONArray("pinned_tabs").objects().map { it.getString("type") to it.optString("ref") },
            playlists = json.optJSONArray("playlists").objects().map { p ->
                p.getString("name") to (p.optJSONArray("items")?.let { a -> List(a.length()) { a.getString(it) } } ?: emptyList())
            },
        )
    }.getOrNull()

    private fun JSONArray?.objects(): List<JSONObject> = if (this == null) emptyList() else List(length()) { getJSONObject(it) }

    /** App 中还没有任何用户数据（播放列表、固定的艺术家）时，添加文件夹发现备份才提示恢复（需求 B-3）。 */
    suspend fun hasNoUserData(): Boolean =
        playlistDao.all().isEmpty() && tabDao.all().none { it.type == "ARTIST" }

    /**
     * 恢复（docs/design.md §3.1）：同名播放列表合并，固定标签追加缺失的项，设置整体覆盖。
     * 要在扫描完成后调用，这样才能把相对路径映射回歌曲。
     */
    suspend fun restore(backup: Backup): RestoreReport = withContext(Dispatchers.IO) {
        val folders = folderDao.all()
        val roots = folders.map { BackupRefs.Root(it.volumeName, it.relativePath) }
        val refToKey = trackDao.all().mapNotNull { t -> BackupRefs.refOf(t.trackKey, roots)?.let { it to t.trackKey } }.toMap()
        val matcher = BackupRefs.Matcher(refToKey)

        var items = 0
        var missing = 0
        val existingLists = playlistDao.all().associateBy { it.name.lowercase() }
        for ((name, refs) in backup.playlists) {
            val id = existingLists[name.lowercase()]?.id ?: playlists.create(name)
            val present = playlistDao.items(id).map { it.trackKey }.toSet()
            val keys = refs.mapNotNull { ref -> matcher.match(ref).also { if (it == null) missing++ } }.filter { it !in present }
            if (keys.isNotEmpty()) playlists.addKeys(id, keys)
            items += keys.size
        }

        // 固定的艺术家和手动固定时一样，排在"文件夹"标签后面
        val pinnedCount = tabs.pinArtists(backup.pinnedTabs.filter { it.first == "ARTIST" }.map { it.second })

        backup.settings?.let { applySettings(it) }
        RestoreReport(backup.playlists.size, items, missing, pinnedCount)
    }

    private suspend fun applySettings(json: JSONObject) {
        val old = settings.current()
        val scan = ScanConfig(
            split = ArtistSplitConfig(
                separators = json.optJSONArray("artist_separators")?.let { a -> List(a.length()) { a.getString(it) }.toSet() } ?: old.scan.split.separators,
                exceptions = json.optJSONArray("artist_exceptions")?.let { a -> List(a.length()) { a.getString(it) } } ?: old.scan.split.exceptions,
            ),
            inferFromFileName = json.optBoolean("infer_from_file_name", old.scan.inferFromFileName),
            fileNameFormat = runCatching { FileNameFormat.valueOf(json.getString("file_name_format")) }.getOrDefault(old.scan.fileNameFormat),
            legacyCharset = json.optString("legacy_charset").takeIf { it.isNotEmpty() && it != "null" }?.let { runCatching { Charset.forName(it) }.getOrNull() },
        )
        settings.setScanConfig(scan)
        runCatching { ThemeMode.valueOf(json.getString("theme")) }.getOrNull()?.let { settings.setThemeMode(it) }
        runCatching { LowPowerMode.valueOf(json.getString("low_power_mode")) }.getOrNull()?.let { settings.setLowPowerMode(it) }
        if (scan.legacyCharset != old.scan.legacyCharset) scanner.requestScan(full = true) else if (scan != old.scan) scanner.rebuildArtists()
    }

    private companion object {
        const val TAG = "BackupManager"
        const val FORMAT = 1
        const val DIR = DocumentTree.APP_DIR
        const val FILE = "backup.json"
        const val PREV = "backup.prev.json"
        const val TMP = "backup.json.tmp"
    }
}
