package com.tinyfish.jeekmusic.ui.navigation

/** 顶层页面（全屏）。 */
sealed interface TopRoute {
    data object Main : TopRoute
    data object Settings : TopRoute
}

/** 标签页内的页面。每个标签有自己的返回栈（docs/design.md §10）。 */
sealed interface TabRoute {
    data object FolderRoot : TabRoute
    data class Folder(val folderId: Long, val relativePath: String, val title: String) : TabRoute
    data object ArtistList : TabRoute
    data class ArtistDetail(val nameKey: String, val isTabRoot: Boolean = false) : TabRoute
    data class Album(val albumKey: String) : TabRoute
}

/** 标签栏上的一个标签。 */
sealed interface Tab {
    val root: TabRoute

    data object Folders : Tab {
        override val root = TabRoute.FolderRoot
    }

    data object Artists : Tab {
        override val root = TabRoute.ArtistList
    }
}
