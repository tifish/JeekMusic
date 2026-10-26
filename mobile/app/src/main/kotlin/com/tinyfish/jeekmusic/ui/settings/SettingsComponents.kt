package com.tinyfish.jeekmusic.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.tinyfish.jeekmusic.R

/** Navigation and selection-dialog entries share a reserved trailing affordance. */
@Composable
fun SettingItem(
    title: String,
    modifier: Modifier = Modifier,
    summary: String? = null,
    icon: Int? = null,
    enabled: Boolean = true,
    onClick: (() -> Unit)? = null,
) {
    Row(
        modifier.fillMaxWidth().heightIn(min = 56.dp)
            .let { if (onClick != null) it.clickable(enabled = enabled, role = Role.Button, onClick = onClick) else it }
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        if (icon != null) Icon(painterResource(icon), null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(title, style = if (onClick == null) MaterialTheme.typography.bodyMedium else MaterialTheme.typography.bodyLarge,
                color = if (enabled && onClick != null) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
            if (summary != null) SettingSummary(summary)
            if (!enabled) SettingSummary(stringResource(R.string.settings_unavailable))
        }
        if (onClick != null) Icon(painterResource(R.drawable.ic_chevron_right), null,
            Modifier.size(24.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Commands have a button outline; modifier targets the button so existing automation tags stay actionable. */
@Composable
fun SettingAction(
    title: String,
    modifier: Modifier = Modifier,
    summary: String? = null,
    icon: Int? = null,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        OutlinedButton(onClick = onClick, enabled = enabled, modifier = modifier.heightIn(min = 48.dp)) {
            if (icon != null) {
                Icon(painterResource(icon), null, Modifier.size(20.dp))
                Spacer(Modifier.width(8.dp))
            }
            Text(title)
        }
        if (summary != null) SettingSummary(summary)
    }
}

@Composable
fun SettingSummary(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

/** The row owns the toggle semantics and event; the indicator is decorative. */
@Composable
fun SwitchRow(title: String, checked: Boolean, onChange: (Boolean) -> Unit, modifier: Modifier = Modifier, summary: String? = null, enabled: Boolean = true) {
    Row(modifier.fillMaxWidth().heightIn(min = 56.dp).toggleable(checked, enabled = enabled, role = Role.Switch, onValueChange = onChange)
        .padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (summary != null) SettingSummary(summary)
        }
        Switch(checked = checked, onCheckedChange = null, enabled = enabled)
    }
}

@Composable
fun SettingCheckbox(title: String, checked: Boolean, onChange: (Boolean) -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true, summary: String? = null) {
    Row(modifier.fillMaxWidth().heightIn(min = 48.dp).toggleable(checked, enabled = enabled, role = Role.Checkbox, onValueChange = onChange)
        .padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Checkbox(checked = checked, onCheckedChange = null, enabled = enabled)
        Column(Modifier.weight(1f)) {
            Text(title)
            if (summary != null) SettingSummary(summary)
        }
    }
}

@Composable
fun SettingRadio(title: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true, summary: String? = null) {
    Row(modifier.fillMaxWidth().heightIn(min = 48.dp).selectable(selected, enabled = enabled, role = Role.RadioButton, onClick = onClick)
        .padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        RadioButton(selected = selected, onClick = null, enabled = enabled)
        Column(Modifier.weight(1f)) {
            Text(title, color = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
            if (summary != null) SettingSummary(summary)
        }
    }
}
