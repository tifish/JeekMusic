package com.tinyfish.jeekmusic.playback

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import com.tinyfish.jeekmusic.data.cover.CoverStore
import com.tinyfish.jeekmusic.data.db.TrackDao
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.runBlocking
import java.io.FileNotFoundException

/**
 * 封面的统一入口（docs/design.md §7.4）：列表缩略图、播放页、通知栏、锁屏都从这里取。
 *
 * content://<authority>/track/<trackId>/<small|large>
 *
 * 查找和缓存都在 [CoverStore]（需求 C-1～C-3）；没有封面时返回 FileNotFound，由调用方显示占位图。
 * URI 上的查询参数（换封面后的版本号）只用来让图片加载库重新加载，这里忽略。
 */
class ArtworkProvider : ContentProvider() {

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Dependencies {
        fun trackDao(): TrackDao
        fun coverStore(): CoverStore
    }

    private val deps by lazy {
        EntryPointAccessors.fromApplication(requireContext().applicationContext, Dependencies::class.java)
    }

    override fun onCreate() = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        val segments = uri.pathSegments
        val trackId = segments.getOrNull(1)?.toLongOrNull() ?: throw FileNotFoundException(uri.toString())
        val large = segments.getOrNull(2) == "large"
        val file = runBlocking {
            val track = deps.trackDao().byId(trackId) ?: throw FileNotFoundException(uri.toString())
            deps.coverStore().cachedFile(track, large)
        } ?: throw FileNotFoundException("no artwork for $uri")
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun getType(uri: Uri) = "image/jpeg"
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0
}
