package com.tinyfish.jeekmusic.ui.settings

import com.tinyfish.jeekmusic.ui.components.selectedChipColors
import com.tinyfish.jeekmusic.ui.components.verticalScrollWithScrollbar
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.scanner.ArtistSplitConfig
import com.tinyfish.jeekmusic.data.scanner.FileNameFormat
import com.tinyfish.jeekmusic.data.scanner.LibraryScanner
import com.tinyfish.jeekmusic.data.settings.ScanConfig
import com.tinyfish.jeekmusic.data.settings.SettingsRepository
import com.tinyfish.jeekmusic.ui.components.SectionHeader
import com.tinyfish.jeekmusic.ui.components.SubScreenHeader
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.launch
import java.nio.charset.Charset
import javax.inject.Inject

@HiltViewModel
class ArtistSettingsViewModel @Inject constructor(
    private val settings: SettingsRepository,
    private val scanner: LibraryScanner,
) : ViewModel() {

    suspend fun current(): ScanConfig = settings.current().scan

    /**
     * 只改了分隔符、例外名单或文件名推断时，用库里已存的字段重算即可；
     * 改了旧标签编码则必须重新读取所有标签（docs/design.md §4.4）。
     */
    fun apply(config: ScanConfig, onDone: () -> Unit) = viewModelScope.launch {
        val old = settings.current().scan
        settings.setScanConfig(config)
        if (old.legacyCharset != config.legacyCharset) scanner.requestScan(full = true) else scanner.rebuildArtists()
        onDone()
    }
}

private val LEGACY_CHARSETS = listOf(null, "GBK", "Big5", "Shift_JIS")

/** 艺术家标签设置（需求 A-3、A-4、A-9、§3.14）。 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ArtistSettingsScreen(onBack: () -> Unit, viewModel: ArtistSettingsViewModel = hiltViewModel()) {
    var config by remember { mutableStateOf<ScanConfig?>(null) }
    var exceptions by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        config = viewModel.current().also { exceptions = it.split.exceptions.joinToString("\n") }
    }
    val c = config

    Column(Modifier.fillMaxSize().systemBarsPadding().testTag("artist_settings")) {
        SubScreenHeader(title = stringResource(R.string.settings_artists), onBack = onBack)
        if (c == null) return@Column
        Column(Modifier.fillMaxSize().verticalScrollWithScrollbar(rememberScrollState(), draggable = true)) {
            SectionHeader(stringResource(R.string.artist_separators))
            Text(stringResource(R.string.artist_separators_summary), Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            FlowRow(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ArtistSplitConfig.ALL_SEPARATORS.forEach { sep ->
                    val selected = sep in c.split.separators
                    FilterChip(
                        colors = selectedChipColors(),
                        selected = selected,
                        onClick = {
                            val next = if (selected) c.split.separators - sep else c.split.separators + sep
                            config = c.copy(split = c.split.copy(separators = next))
                        },
                        label = { Text(if (sep.length == 1) " $sep " else sep) },
                    )
                }
            }

            SectionHeader(stringResource(R.string.artist_exceptions))
            OutlinedTextField(
                value = exceptions,
                onValueChange = { exceptions = it },
                placeholder = { Text(stringResource(R.string.artist_exceptions_hint)) },
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                minLines = 3,
            )

            SectionHeader(stringResource(R.string.infer_from_file_name))
            SwitchRow(
                title = stringResource(R.string.infer_from_file_name_summary), checked = c.inferFromFileName,
                onChange = { config = c.copy(inferFromFileName = it) },
            )
            if (!c.inferFromFileName) SettingSummary(stringResource(R.string.artist_format_disabled), Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
            listOf(FileNameFormat.ARTIST_TITLE to R.string.format_artist_title, FileNameFormat.TITLE_ARTIST to R.string.format_title_artist).forEach { (format, label) ->
                SettingRadio(title = stringResource(label), selected = c.fileNameFormat == format, enabled = c.inferFromFileName,
                    onClick = { config = c.copy(fileNameFormat = format) })
            }

            SectionHeader(stringResource(R.string.legacy_charset))
            LEGACY_CHARSETS.forEach { name ->
                val selected = c.legacyCharset?.name() == name?.let { Charset.forName(it).name() }
                SettingRadio(title = name ?: stringResource(R.string.legacy_charset_auto), selected = selected,
                    onClick = { config = c.copy(legacyCharset = name?.let(Charset::forName)) })
            }

            Button(
                onClick = {
                    val final = c.copy(split = c.split.copy(exceptions = exceptions.lines().map { it.trim() }.filter { it.isNotEmpty() }))
                    viewModel.apply(final, onBack)
                },
                modifier = Modifier.fillMaxWidth().padding(16.dp).testTag("apply_artist_settings"),
            ) {
                Text(stringResource(R.string.apply_and_rebuild))
            }
        }
    }
}
