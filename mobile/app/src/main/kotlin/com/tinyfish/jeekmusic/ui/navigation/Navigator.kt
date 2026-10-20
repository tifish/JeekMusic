package com.tinyfish.jeekmusic.ui.navigation

import androidx.annotation.StringRes
import androidx.compose.runtime.staticCompositionLocalOf
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 页面跳转入口。艺术家、专辑、文件夹等详情页压入当前标签的返回栈（docs/design.md §10）。
 */
interface Navigator {
    fun openArtist(nameKey: String)
    fun openAlbum(albumKey: String)
    fun openFolder(folderId: Long, relativePath: String, title: String)
    fun open(route: TopRoute)
}

val LocalNavigator = staticCompositionLocalOf<Navigator> { error("Navigator not provided") }

/** 一条给用户的提示（Snackbar）。 */
data class UiMessage(@param:StringRes val res: Int, val arg: String? = null)

/** 全局提示总线：任何地方都可以发出提示，由主界面统一显示。 */
@Singleton
class MessageBus @Inject constructor() {
    private val _messages = MutableSharedFlow<UiMessage>(extraBufferCapacity = 8)
    val messages: SharedFlow<UiMessage> = _messages.asSharedFlow()

    fun post(message: UiMessage) {
        _messages.tryEmit(message)
    }
}
