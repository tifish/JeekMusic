package com.tinyfish.jeekmusic.ui.folders

import android.Manifest
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.core.content.ContextCompat
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.tinyfish.jeekmusic.R
import com.tinyfish.jeekmusic.data.library.AddFolderResult
import com.tinyfish.jeekmusic.data.library.LibraryRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

@HiltViewModel
class AddFolderViewModel @Inject constructor(
    private val library: LibraryRepository,
) : ViewModel() {

    data class Pending(val uri: Uri, val result: AddFolderResult)

    private val _pending = MutableStateFlow<Pending?>(null)
    val pending = _pending.asStateFlow()

    fun add(uri: Uri, replaceChildren: Boolean = false) {
        viewModelScope.launch {
            val result = library.addFolder(uri, replaceChildren)
            _pending.value = if (result == AddFolderResult.Added) null else Pending(uri, result)
        }
    }

    fun dismiss() {
        _pending.value = null
    }
}

private val audioPermission =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) Manifest.permission.READ_MEDIA_AUDIO
    else Manifest.permission.READ_EXTERNAL_STORAGE

/**
 * 添加音乐文件夹的完整流程：申请读取音频权限 → 系统文件夹选择器 → 检查嵌套关系（需求 L-1、L-3、L-4）。
 * 返回一个启动函数。
 */
@Composable
fun rememberAddFolder(viewModel: AddFolderViewModel = hiltViewModel()): () -> Unit {
    val context = LocalContext.current
    val pending by viewModel.pending.collectAsStateWithLifecycle()

    val pickFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) viewModel.add(uri)
    }
    val requestPermissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        if (result[audioPermission] == true) {
            pickFolder.launch(null)
        } else {
            Toast.makeText(context, R.string.permission_needed, Toast.LENGTH_LONG).show()
        }
    }

    when (val result = pending?.result) {
        is AddFolderResult.AlreadyCovered -> {
            val message = stringResource(R.string.folder_already_covered, result.by.displayName)
            LaunchedEffect(result) {
                Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                viewModel.dismiss()
            }
        }
        is AddFolderResult.Unsupported -> {
            val message = stringResource(R.string.folder_unsupported)
            LaunchedEffect(result) {
                Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                viewModel.dismiss()
            }
        }
        is AddFolderResult.WouldReplace -> AlertDialog(
            onDismissRequest = viewModel::dismiss,
            title = { Text(stringResource(R.string.folder_replace_title)) },
            text = { Text(stringResource(R.string.folder_replace_message, result.children.joinToString("\n") { it.displayName })) },
            confirmButton = {
                TextButton(onClick = { pending?.let { viewModel.add(it.uri, replaceChildren = true) } }) {
                    Text(stringResource(R.string.replace))
                }
            },
            dismissButton = { TextButton(onClick = viewModel::dismiss) { Text(stringResource(R.string.cancel)) } },
        )
        else -> Unit
    }

    return remember(pickFolder, requestPermissions) {
        {
            val granted = ContextCompat.checkSelfPermission(context, audioPermission) == PackageManager.PERMISSION_GRANTED
            if (granted) {
                pickFolder.launch(null)
            } else {
                val permissions = buildList {
                    add(audioPermission)
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) add(Manifest.permission.POST_NOTIFICATIONS)
                }
                requestPermissions.launch(permissions.toTypedArray())
            }
        }
    }
}
