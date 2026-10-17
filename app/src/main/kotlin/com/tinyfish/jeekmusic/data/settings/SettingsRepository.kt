package com.tinyfish.jeekmusic.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.tinyfish.jeekmusic.analysis.Sensitivity
import com.tinyfish.jeekmusic.analysis.Tag
import com.tinyfish.jeekmusic.analysis.TagDimension
import com.tinyfish.jeekmusic.data.scanner.ArtistSplitConfig
import com.tinyfish.jeekmusic.data.scanner.FileNameFormat
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import java.nio.charset.Charset
import javax.inject.Inject
import javax.inject.Singleton

enum class ThemeMode { SYSTEM, LIGHT, DARK }

/**
 * 在线歌词保存位置（需求 LY-11）：
 * - SIDECAR：歌曲旁边的同名 .lrc，其他播放器也能读
 * - HIDDEN_FOLDER：音乐目录根下的 .jeekmusic/lyrics/，不改动音乐文件
 * - EMBED：写进音乐文件的歌词标签
 * - APP_CACHE：只存在 App 里
 */
enum class LyricsSaveLocation { SIDECAR, HIDDEN_FOLDER, EMBED, APP_CACHE }

/**
 * 在线封面的保存方式（需求 C-3）：
 * - HIDDEN_FOLDER：音乐目录根下的 .jeekmusic/covers/，按"艺术家 - 专辑（或歌名）.jpg"命名，不改动音乐文件
 * - EMBED：写进音乐文件的标签
 * - SIDECAR：歌曲旁边的同名 .jpg
 */
enum class CoverSaveMode { HIDDEN_FOLDER, EMBED, SIDECAR }

/**
 * 在线歌词、封面的来源（docs/design.md §8.4）。枚举的顺序就是自动获取时尝试的顺序：国内访问稳定的排在前面。
 * - 网易云音乐、QQ 音乐：非官方的网页接口，国内稳定，中文歌全
 * - iTunes：国内可以访问，英文歌全，中文歌只有繁体能搜到
 * - MusicBrainz：资料全但访问慢；封面图片在 archive.org，国内通常无法访问
 * - LRCLIB：开放的歌词库，国内访问不稳定
 */
enum class OnlineSource {
    NETEASE, QQ, ITUNES, MUSICBRAINZ, LRCLIB;

    companion object {
        val FOR_COVERS = listOf(NETEASE, QQ, ITUNES, MUSICBRAINZ)
        val FOR_LYRICS = listOf(NETEASE, QQ, LRCLIB)
    }
}

/** 在线歌词、封面的获取方式（需求 LY-8、C-3）：手动 / 自动（任何网络）/ 仅 Wi-Fi（不计流量的网络）下自动。 */
enum class FetchMode { MANUAL, AUTO, WIFI }

/** 智能标签的分析时机（需求 M-5）。 */
enum class AnalysisMode { CHARGING, ANYTIME }

/** 低功耗播放（音频 offload）的三档设置（docs/design.md §7.2）。 */
enum class LowPowerMode { BACKGROUND, ALWAYS, OFF }

/** 扫描相关设置。修改后需要重建艺术家索引（docs/design.md §4.4）。 */
data class ScanConfig(
    val split: ArtistSplitConfig = ArtistSplitConfig(),
    val inferFromFileName: Boolean = true,
    val fileNameFormat: FileNameFormat = FileNameFormat.ARTIST_TITLE,
    /** 强制指定的旧标签编码；null 表示自动判断。 */
    val legacyCharset: Charset? = null,
)

/** 全局的智能标签筛选（需求 M-6）：每个维度不选或选一个，多个维度同时满足。 */
data class TagFilter(val mood: Tag? = null, val voice: Tag? = null, val genre: Tag? = null) {
    val active: List<Tag> get() = listOfNotNull(mood, voice, genre)
    val isActive: Boolean get() = active.isNotEmpty()

    operator fun get(dimension: TagDimension): Tag? = when (dimension) {
        TagDimension.MOOD -> mood
        TagDimension.VOICE -> voice
        TagDimension.GENRE -> genre
    }

    /** 点一下选中，再点一下取消。 */
    fun toggle(tag: Tag): TagFilter {
        val value = if (this[tag.dimension] == tag) null else tag
        return when (tag.dimension) {
            TagDimension.MOOD -> copy(mood = value)
            TagDimension.VOICE -> copy(voice = value)
            TagDimension.GENRE -> copy(genre = value)
        }
    }
}

/** 歌曲列表的排序（需求 U-8）。DEFAULT 时各列表用自己的默认顺序。 */
enum class SongSort { DEFAULT, MODIFIED, FILE_NAME, TITLE, ARTIST }

/** 睡眠定时对话框里上次的选择（需求 P-8），下次打开时沿用。 */
data class SleepPrefs(
    val finishSong: Boolean = false,
    val customMinutes: Int = 20,
    val songCount: Int = 3,
)

data class Settings(
    val themeMode: ThemeMode = ThemeMode.SYSTEM,
    val scan: ScanConfig = ScanConfig(),
    val unsupportedFileCount: Int = 0,
    val lowPowerMode: LowPowerMode = LowPowerMode.BACKGROUND,
    val analysisMode: AnalysisMode = AnalysisMode.CHARGING,
    val sensitivity: Sensitivity = Sensitivity.NORMAL,
    /** 在线歌词，默认关闭（需求 LY-8）。 */
    val onlineLyrics: Boolean = false,
    /** 播放到没有歌词的歌时是否自动获取。 */
    val lyricsFetchMode: FetchMode = FetchMode.MANUAL,
    val lyricsSaveLocation: LyricsSaveLocation = LyricsSaveLocation.SIDECAR,
    /** 在线封面，默认关闭（需求 C-3）。 */
    val onlineCovers: Boolean = false,
    val coverSaveMode: CoverSaveMode = CoverSaveMode.HIDDEN_FOLDER,
    val coverFetchMode: FetchMode = FetchMode.MANUAL,
    /** 启用的封面来源，按 [OnlineSource] 的顺序尝试。 */
    val coverSources: Set<OnlineSource> = OnlineSource.FOR_COVERS.toSet(),
    val lyricsSources: Set<OnlineSource> = OnlineSource.FOR_LYRICS.toSet(),
    val sleep: SleepPrefs = SleepPrefs(),
    val tagFilter: TagFilter = TagFilter(),
    val songSort: SongSort = SongSort.DEFAULT,
    /** 每天自动检查一次 GitHub 上的新版本（需求 U-6）。 */
    val autoUpdateCheck: Boolean = true,
    val lastUpdateCheck: Long = 0,
    /** 用户选择"跳过此版本"的 versionCode；0 表示没有。 */
    val skippedUpdateVersion: Int = 0,
    /** 智能标签分类规则的版本，见 TagRepository.recomputeIfRulesChanged。 */
    val tagRulesVersion: Int = 0,
    /** 标签栏数据的整理版本，见 TabRepository.ensureDefaults。 */
    val tabLayoutVersion: Int = 0,
)

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

@Singleton
class SettingsRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
) {
    private object Keys {
        val THEME = stringPreferencesKey("theme")
        val SEPARATORS = stringSetPreferencesKey("artist_separators")
        val EXCEPTIONS = stringPreferencesKey("artist_exceptions")
        val INFER = booleanPreferencesKey("infer_from_file_name")
        val FILE_NAME_FORMAT = stringPreferencesKey("file_name_format")
        val LEGACY_CHARSET = stringPreferencesKey("legacy_charset")
        val UNSUPPORTED = intPreferencesKey("unsupported_file_count")
        val LOW_POWER = stringPreferencesKey("low_power_mode")
        val ANALYSIS_MODE = stringPreferencesKey("analysis_mode")
        val SENSITIVITY = stringPreferencesKey("mood_sensitivity")
        val ONLINE_LYRICS = booleanPreferencesKey("online_lyrics")
        /** 旧版本"仅 Wi-Fi 下自动获取"开关，读取时兼容。 */
        val AUTO_FETCH = booleanPreferencesKey("auto_fetch_lyrics_wifi")
        val LYRICS_FETCH_MODE = stringPreferencesKey("lyrics_fetch_mode")
        val LYRICS_SAVE = stringPreferencesKey("lyrics_save_location")
        val ONLINE_COVERS = booleanPreferencesKey("online_covers")
        val COVER_SAVE_MODE = stringPreferencesKey("cover_save_mode")
        val COVER_FETCH_MODE = stringPreferencesKey("cover_fetch_mode")
        val COVER_SOURCES = stringSetPreferencesKey("cover_sources")
        val LYRICS_SOURCES = stringSetPreferencesKey("lyrics_sources")
        val SLEEP_FINISH_SONG = booleanPreferencesKey("sleep_finish_song")
        val SLEEP_CUSTOM_MINUTES = intPreferencesKey("sleep_custom_minutes")
        val SLEEP_SONG_COUNT = intPreferencesKey("sleep_song_count")
        /** 旧版本只有"运动 / 安静"筛选时的键，读取时兼容。 */
        val MOOD_FILTER = stringPreferencesKey("mood_filter")
        val FILTER_MOOD = stringPreferencesKey("filter_mood")
        val FILTER_VOICE = stringPreferencesKey("filter_voice")
        val FILTER_GENRE = stringPreferencesKey("filter_genre")
        val SONG_SORT = stringPreferencesKey("song_sort")
        val AUTO_UPDATE = booleanPreferencesKey("auto_update_check")
        val LAST_UPDATE_CHECK = longPreferencesKey("last_update_check")
        val SKIPPED_UPDATE = intPreferencesKey("skipped_update_version")
        val TAB_LAYOUT_VERSION = intPreferencesKey("tab_layout_version")
        val TAG_RULES_VERSION = intPreferencesKey("tag_rules_version")
    }

    val settings: Flow<Settings> = context.dataStore.data.map { it.toSettings() }

    suspend fun current(): Settings = settings.first()

    suspend fun setThemeMode(mode: ThemeMode) = context.dataStore.edit { it[Keys.THEME] = mode.name }

    suspend fun setLowPowerMode(mode: LowPowerMode) = context.dataStore.edit { it[Keys.LOW_POWER] = mode.name }

    suspend fun setAnalysisMode(mode: AnalysisMode) = context.dataStore.edit { it[Keys.ANALYSIS_MODE] = mode.name }

    suspend fun setSensitivity(sensitivity: Sensitivity) = context.dataStore.edit { it[Keys.SENSITIVITY] = sensitivity.name }

    suspend fun setOnlineLyrics(enabled: Boolean) = context.dataStore.edit { it[Keys.ONLINE_LYRICS] = enabled }

    suspend fun setLyricsFetchMode(mode: FetchMode) = context.dataStore.edit {
        it.remove(Keys.AUTO_FETCH)
        it[Keys.LYRICS_FETCH_MODE] = mode.name
    }

    suspend fun setLyricsSaveLocation(location: LyricsSaveLocation) = context.dataStore.edit { it[Keys.LYRICS_SAVE] = location.name }

    suspend fun setOnlineCovers(enabled: Boolean) = context.dataStore.edit { it[Keys.ONLINE_COVERS] = enabled }

    suspend fun setCoverSaveMode(mode: CoverSaveMode) = context.dataStore.edit { it[Keys.COVER_SAVE_MODE] = mode.name }

    suspend fun setCoverSources(sources: Set<OnlineSource>) = context.dataStore.edit { it[Keys.COVER_SOURCES] = sources.map { s -> s.name }.toSet() }

    suspend fun setLyricsSources(sources: Set<OnlineSource>) = context.dataStore.edit { it[Keys.LYRICS_SOURCES] = sources.map { s -> s.name }.toSet() }

    suspend fun setCoverFetchMode(mode: FetchMode) = context.dataStore.edit { it[Keys.COVER_FETCH_MODE] = mode.name }

    suspend fun setSleepPrefs(prefs: SleepPrefs) = context.dataStore.edit {
        it[Keys.SLEEP_FINISH_SONG] = prefs.finishSong
        it[Keys.SLEEP_CUSTOM_MINUTES] = prefs.customMinutes
        it[Keys.SLEEP_SONG_COUNT] = prefs.songCount
    }

    suspend fun setAutoUpdateCheck(enabled: Boolean) = context.dataStore.edit { it[Keys.AUTO_UPDATE] = enabled }

    suspend fun setLastUpdateCheck(time: Long) = context.dataStore.edit { it[Keys.LAST_UPDATE_CHECK] = time }

    suspend fun setSkippedUpdateVersion(versionCode: Int) = context.dataStore.edit { it[Keys.SKIPPED_UPDATE] = versionCode }

    suspend fun setTagRulesVersion(version: Int) = context.dataStore.edit { it[Keys.TAG_RULES_VERSION] = version }

    suspend fun setTabLayoutVersion(version: Int) = context.dataStore.edit { it[Keys.TAB_LAYOUT_VERSION] = version }

    suspend fun setSongSort(sort: SongSort) = context.dataStore.edit { it[Keys.SONG_SORT] = sort.name }

    suspend fun setTagFilter(filter: TagFilter) = context.dataStore.edit { prefs ->
        prefs.remove(Keys.MOOD_FILTER)
        fun put(key: Preferences.Key<String>, tag: Tag?) = if (tag == null) prefs[key] = "" else prefs[key] = tag.name
        put(Keys.FILTER_MOOD, filter.mood)
        put(Keys.FILTER_VOICE, filter.voice)
        put(Keys.FILTER_GENRE, filter.genre)
    }

    suspend fun setUnsupportedFileCount(count: Int) = context.dataStore.edit { it[Keys.UNSUPPORTED] = count }

    suspend fun setScanConfig(config: ScanConfig) = context.dataStore.edit {
        it[Keys.SEPARATORS] = config.split.separators
        it[Keys.EXCEPTIONS] = config.split.exceptions.joinToString("\n")
        it[Keys.INFER] = config.inferFromFileName
        it[Keys.FILE_NAME_FORMAT] = config.fileNameFormat.name
        if (config.legacyCharset == null) it.remove(Keys.LEGACY_CHARSET) else it[Keys.LEGACY_CHARSET] = config.legacyCharset.name()
    }

    private fun Preferences.toSettings() = Settings(
        themeMode = this[Keys.THEME]?.let { runCatching { ThemeMode.valueOf(it) }.getOrNull() } ?: ThemeMode.SYSTEM,
        scan = ScanConfig(
            split = ArtistSplitConfig(
                separators = this[Keys.SEPARATORS] ?: ArtistSplitConfig.DEFAULT_SEPARATORS,
                exceptions = this[Keys.EXCEPTIONS]?.split('\n')?.filter { it.isNotBlank() } ?: ArtistSplitConfig.DEFAULT_EXCEPTIONS,
            ),
            inferFromFileName = this[Keys.INFER] ?: true,
            fileNameFormat = this[Keys.FILE_NAME_FORMAT]?.let { runCatching { FileNameFormat.valueOf(it) }.getOrNull() }
                ?: FileNameFormat.ARTIST_TITLE,
            legacyCharset = this[Keys.LEGACY_CHARSET]?.let { runCatching { Charset.forName(it) }.getOrNull() },
        ),
        unsupportedFileCount = this[Keys.UNSUPPORTED] ?: 0,
        lowPowerMode = this[Keys.LOW_POWER]?.let { runCatching { LowPowerMode.valueOf(it) }.getOrNull() } ?: LowPowerMode.BACKGROUND,
        analysisMode = this[Keys.ANALYSIS_MODE]?.let { runCatching { AnalysisMode.valueOf(it) }.getOrNull() } ?: AnalysisMode.CHARGING,
        sensitivity = this[Keys.SENSITIVITY]?.let { runCatching { Sensitivity.valueOf(it) }.getOrNull() } ?: Sensitivity.NORMAL,
        onlineLyrics = this[Keys.ONLINE_LYRICS] ?: false,
        lyricsFetchMode = this[Keys.LYRICS_FETCH_MODE]?.let { runCatching { FetchMode.valueOf(it) }.getOrNull() }
            ?: if (this[Keys.AUTO_FETCH] == true) FetchMode.WIFI else FetchMode.MANUAL,
        lyricsSaveLocation = when (val saved = this[Keys.LYRICS_SAVE]) {
            // 旧版本的"音乐目录"就是同名 .lrc
            "MUSIC_FOLDER" -> LyricsSaveLocation.SIDECAR
            else -> saved?.let { runCatching { LyricsSaveLocation.valueOf(it) }.getOrNull() } ?: LyricsSaveLocation.SIDECAR
        },
        onlineCovers = this[Keys.ONLINE_COVERS] ?: false,
        coverSaveMode = this[Keys.COVER_SAVE_MODE]?.let { runCatching { CoverSaveMode.valueOf(it) }.getOrNull() } ?: CoverSaveMode.HIDDEN_FOLDER,
        coverSources = sources(this[Keys.COVER_SOURCES], OnlineSource.FOR_COVERS),
        lyricsSources = sources(this[Keys.LYRICS_SOURCES], OnlineSource.FOR_LYRICS),
        coverFetchMode = this[Keys.COVER_FETCH_MODE]?.let { runCatching { FetchMode.valueOf(it) }.getOrNull() } ?: FetchMode.MANUAL,
        sleep = SleepPrefs(
            finishSong = this[Keys.SLEEP_FINISH_SONG] ?: false,
            customMinutes = this[Keys.SLEEP_CUSTOM_MINUTES] ?: 20,
            songCount = this[Keys.SLEEP_SONG_COUNT] ?: 3,
        ),
        tagFilter = TagFilter(
            mood = Tag.of(TagDimension.MOOD, this[Keys.FILTER_MOOD] ?: this[Keys.MOOD_FILTER]),
            voice = Tag.of(TagDimension.VOICE, this[Keys.FILTER_VOICE]),
            genre = Tag.of(TagDimension.GENRE, this[Keys.FILTER_GENRE]),
        ),
        songSort = this[Keys.SONG_SORT]?.let { runCatching { SongSort.valueOf(it) }.getOrNull() } ?: SongSort.DEFAULT,
        autoUpdateCheck = this[Keys.AUTO_UPDATE] ?: true,
        lastUpdateCheck = this[Keys.LAST_UPDATE_CHECK] ?: 0,
        skippedUpdateVersion = this[Keys.SKIPPED_UPDATE] ?: 0,
        tabLayoutVersion = this[Keys.TAB_LAYOUT_VERSION] ?: 0,
        tagRulesVersion = this[Keys.TAG_RULES_VERSION] ?: 0,
    )

    /** 没设置过时全部启用；只认 [allowed] 里的来源。 */
    private fun sources(saved: Set<String>?, allowed: List<OnlineSource>): Set<OnlineSource> =
        saved?.mapNotNull { name -> allowed.firstOrNull { it.name == name } }?.toSet() ?: allowed.toSet()
}
