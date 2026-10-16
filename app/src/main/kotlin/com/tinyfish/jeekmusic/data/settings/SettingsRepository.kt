package com.tinyfish.jeekmusic.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
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
    )
}
