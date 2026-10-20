package com.tinyfish.jeekmusic.ui.navigation

/** 顶层页面（全屏）。 */
sealed interface TopRoute {
    data object Main : TopRoute
    data object Settings : TopRoute
    data object Search : TopRoute
    data object TabManager : TopRoute
    data object ArtistSettings : TopRoute
}

/** 标签页内的页面。每个标签有自己的返回栈（docs/design.md §10）。 */
sealed interface TabRoute {
    data object FolderRoot : TabRoute
    data class Folder(val folderId: Long, val relativePath: String, val title: String) : TabRoute
    data object ArtistList : TabRoute
    data class ArtistDetail(val nameKey: String, val isTabRoot: Boolean = false) : TabRoute
    data class Album(val albumKey: String) : TabRoute
    data object PlaylistList : TabRoute
    data class PlaylistDetail(val playlistId: Long) : TabRoute
}

/** 标签栏上的一个标签（需求 T-1 ~ T-4）。[key] 在所有标签中唯一，用于保存返回栈和上次选中的标签。 */
sealed interface Tab {
    val key: String
    val root: TabRoute

    data object Folders : Tab {
        override val key = "FOLDERS"
        override val root = TabRoute.FolderRoot
    }

    data object Artists : Tab {
        override val key = "ARTISTS"
        override val root = TabRoute.ArtistList
    }

    data object Playlists : Tab {
        override val key = "PLAYLISTS"
        override val root = TabRoute.PlaylistList
    }

    data class PinnedArtist(val nameKey: String, val name: String) : Tab {
        override val key = "ARTIST:$nameKey"
        override val root = TabRoute.ArtistDetail(nameKey, isTabRoot = true)
    }
}
