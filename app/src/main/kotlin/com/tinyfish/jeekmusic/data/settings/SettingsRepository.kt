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

/** 在线歌词保存位置（需求 LY-11）。 */
enum class LyricsSaveLocation { MUSIC_FOLDER, APP_CACHE }

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

/** 全局的"运动 / 安静"筛选（需求 M-6）；[mood] 对应 track_mood.mood，ALL 为 null。 */
enum class MoodFilter(val mood: String?) { ALL(null), SPORT("SPORT"), QUIET("QUIET") }

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
    val autoFetchLyricsOnWifi: Boolean = false,
    val lyricsSaveLocation: LyricsSaveLocation = LyricsSaveLocation.MUSIC_FOLDER,
    val sleep: SleepPrefs = SleepPrefs(),
    val moodFilter: MoodFilter = MoodFilter.ALL,
    /** 每天自动检查一次 GitHub 上的新版本（需求 U-6）。 */
    val autoUpdateCheck: Boolean = true,
    val lastUpdateCheck: Long = 0,
    /** 用户选择"跳过此版本"的 versionCode；0 表示没有。 */
    val skippedUpdateVersion: Int = 0,
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
        val AUTO_FETCH = booleanPreferencesKey("auto_fetch_lyrics_wifi")
        val LYRICS_SAVE = stringPreferencesKey("lyrics_save_location")
        val SLEEP_FINISH_SONG = booleanPreferencesKey("sleep_finish_song")
        val SLEEP_CUSTOM_MINUTES = intPreferencesKey("sleep_custom_minutes")
        val SLEEP_SONG_COUNT = intPreferencesKey("sleep_song_count")
        val MOOD_FILTER = stringPreferencesKey("mood_filter")
        val AUTO_UPDATE = booleanPreferencesKey("auto_update_check")
        val LAST_UPDATE_CHECK = longPreferencesKey("last_update_check")
        val SKIPPED_UPDATE = intPreferencesKey("skipped_update_version")
    }

    val settings: Flow<Settings> = context.dataStore.data.map { it.toSettings() }

    suspend fun current(): Settings = settings.first()

    suspend fun setThemeMode(mode: ThemeMode) = context.dataStore.edit { it[Keys.THEME] = mode.name }

    suspend fun setLowPowerMode(mode: LowPowerMode) = context.dataStore.edit { it[Keys.LOW_POWER] = mode.name }

    suspend fun setAnalysisMode(mode: AnalysisMode) = context.dataStore.edit { it[Keys.ANALYSIS_MODE] = mode.name }

    suspend fun setSensitivity(sensitivity: Sensitivity) = context.dataStore.edit { it[Keys.SENSITIVITY] = sensitivity.name }

    suspend fun setOnlineLyrics(enabled: Boolean) = context.dataStore.edit { it[Keys.ONLINE_LYRICS] = enabled }

    suspend fun setAutoFetchLyrics(enabled: Boolean) = context.dataStore.edit { it[Keys.AUTO_FETCH] = enabled }

    suspend fun setLyricsSaveLocation(location: LyricsSaveLocation) = context.dataStore.edit { it[Keys.LYRICS_SAVE] = location.name }

    suspend fun setSleepPrefs(prefs: SleepPrefs) = context.dataStore.edit {
        it[Keys.SLEEP_FINISH_SONG] = prefs.finishSong
        it[Keys.SLEEP_CUSTOM_MINUTES] = prefs.customMinutes
        it[Keys.SLEEP_SONG_COUNT] = prefs.songCount
    }

    suspend fun setAutoUpdateCheck(enabled: Boolean) = context.dataStore.edit { it[Keys.AUTO_UPDATE] = enabled }

    suspend fun setLastUpdateCheck(time: Long) = context.dataStore.edit { it[Keys.LAST_UPDATE_CHECK] = time }

    suspend fun setSkippedUpdateVersion(versionCode: Int) = context.dataStore.edit { it[Keys.SKIPPED_UPDATE] = versionCode }

    suspend fun setMoodFilter(filter: MoodFilter) = context.dataStore.edit { it[Keys.MOOD_FILTER] = filter.name }

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
        autoFetchLyricsOnWifi = this[Keys.AUTO_FETCH] ?: false,
        lyricsSaveLocation = this[Keys.LYRICS_SAVE]?.let { runCatching { LyricsSaveLocation.valueOf(it) }.getOrNull() } ?: LyricsSaveLocation.MUSIC_FOLDER,
        sleep = SleepPrefs(
            finishSong = this[Keys.SLEEP_FINISH_SONG] ?: false,
            customMinutes = this[Keys.SLEEP_CUSTOM_MINUTES] ?: 20,
            songCount = this[Keys.SLEEP_SONG_COUNT] ?: 3,
        ),
        moodFilter = this[Keys.MOOD_FILTER]?.let { runCatching { MoodFilter.valueOf(it) }.getOrNull() } ?: MoodFilter.ALL,
        autoUpdateCheck = this[Keys.AUTO_UPDATE] ?: true,
        lastUpdateCheck = this[Keys.LAST_UPDATE_CHECK] ?: 0,
        skippedUpdateVersion = this[Keys.SKIPPED_UPDATE] ?: 0,
    )
}
