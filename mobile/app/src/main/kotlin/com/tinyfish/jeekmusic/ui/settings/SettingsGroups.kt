package com.tinyfish.jeekmusic.ui.settings

import androidx.annotation.StringRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.ui.components.SectionHeader

/** Flat destinations; Back always returns to the settings overview. */
internal enum class SettingsPage(@param:StringRes val title: Int, @param:StringRes val summary: Int) {
    LIBRARY(R.string.settings_group_library, R.string.settings_group_library_hint),
    PLAYBACK(R.string.settings_playback, R.string.settings_low_power),
    APPEARANCE(R.string.settings_appearance, R.string.settings_group_appearance_summary),
    LYRICS(R.string.settings_lyrics, R.string.settings_group_online_summary),
    COVERS(R.string.settings_covers, R.string.settings_group_online_summary),
    ANALYSIS(R.string.settings_moods, R.string.settings_group_analysis_summary),
    DEVICES(R.string.settings_group_devices, R.string.settings_group_device_summary),
    DATA(R.string.settings_group_data, R.string.settings_group_data_summary),
    ABOUT(R.string.settings_group_about, R.string.settings_group_about_summary),
}

@Composable
internal fun SettingsSectionHeader(title: String) {
    Column(Modifier.fillMaxWidth().padding(top = 20.dp, bottom = 8.dp)) {
        HorizontalDivider(Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.outlineVariant)
        SectionHeader(title, Modifier.padding(top = 8.dp).semantics { heading() })
    }
}

internal fun LazyListScope.settingsHome(
    onOpen: (SettingsPage) -> Unit,
    librarySummary: String,
    lyricsEnabled: Boolean,
    coversEnabled: Boolean,
    lyricsStatus: String?,
    coversStatus: String?,
    pendingAnalysis: Int,
    deviceSummary: String,
    version: String,
    checkingUpdate: Boolean,
    onCheckUpdate: () -> Unit,
) {
    item(key = "update") {
        SettingAction(
            title = stringResource(R.string.update_check_now),
            summary = if (checkingUpdate) stringResource(R.string.update_checking) else stringResource(R.string.settings_version, version),
            enabled = !checkingUpdate, onClick = onCheckUpdate, modifier = Modifier.testTag("check_update"),
        )
    }
    item(key = "music") {
        SettingsHomeGroup(R.string.settings_group_music, listOf(SettingsPage.LIBRARY, SettingsPage.PLAYBACK, SettingsPage.APPEARANCE), onOpen) {
            if (it == SettingsPage.LIBRARY) librarySummary else stringResource(it.summary)
        }
    }
    item(key = "transfer") {
        SettingsHomeGroup(R.string.settings_group_transfer, listOf(SettingsPage.DEVICES, SettingsPage.DATA), onOpen) {
            if (it == SettingsPage.DEVICES) deviceSummary else stringResource(it.summary)
        }
    }
    item(key = "resources") {
        SettingsHomeGroup(R.string.settings_group_resources, listOf(SettingsPage.LYRICS, SettingsPage.COVERS, SettingsPage.ANALYSIS), onOpen) {
            when (it) {
                SettingsPage.LYRICS -> lyricsStatus ?: stringResource(if (lyricsEnabled) R.string.settings_group_online_on else R.string.settings_group_online_off)
                SettingsPage.COVERS -> coversStatus ?: stringResource(if (coversEnabled) R.string.settings_group_online_on else R.string.settings_group_online_off)
                else -> pluralStringResource(R.plurals.settings_group_pending, pendingAnalysis, pendingAnalysis)
            }
        }
    }
    item(key = "application") {
        SettingsHomeGroup(R.string.settings_group_app, listOf(SettingsPage.ABOUT), onOpen) { stringResource(it.summary) }
    }
}

@Composable
private fun SettingsHomeGroup(
    @StringRes title: Int,
    pages: List<SettingsPage>,
    onOpen: (SettingsPage) -> Unit,
    summary: @Composable (SettingsPage) -> String,
) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(top = 16.dp)) {
        Text(stringResource(title), Modifier.padding(start = 4.dp, bottom = 8.dp).semantics { heading() },
            style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        Surface(shape = MaterialTheme.shapes.large, color = MaterialTheme.colorScheme.surfaceContainerLow) {
            Column {
                pages.forEachIndexed { index, page ->
                    if (index > 0) HorizontalDivider(Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.outlineVariant)
                    SettingItem(stringResource(page.title), summary = summary(page), onClick = { onOpen(page) },
                        modifier = Modifier.testTag("settings_group_${page.name.lowercase()}"))
                }
            }
        }
    }
}
