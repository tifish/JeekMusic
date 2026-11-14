package com.tinyfish.jeekmusic.data.filter

import com.tinyfish.jeekmusic.data.db.SearchDao
import com.tinyfish.jeekmusic.data.db.TrackEntity
import com.tinyfish.jeekmusic.data.scanner.MediaStoreSource
import com.tinyfish.jeekmusic.data.scanner.SortKeys
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/** Search original fields and Han initials, using the same rules for artists and songs. */
class InitialsMatcher(private val keys: SortKeys) {
    private val cache = ConcurrentHashMap<String, String>()
    fun initials(text: String): String = cache.getOrPut(text) {
        if (cache.size > 40_000) cache.clear()
        buildString {
            text.codePoints().forEach { cp ->
                if (Character.UnicodeScript.of(cp) == Character.UnicodeScript.HAN) {
                    keys.sortKey(String(Character.toChars(cp))).firstOrNull()?.takeIf { it in 'a'..'z' }?.let(::append)
                }
            }
        }
    }
    fun matches(query: String, vararg fields: String?): Boolean {
        val q = query.trim().lowercase(Locale.ROOT)
        if (q.isEmpty()) return false
        return fields.any { it?.contains(q, ignoreCase = true) == true } ||
            q.all { it in 'a'..'z' || it in '0'..'9' } && fields.any { it != null && initials(it).contains(q) }
    }
}

@Singleton
class LibrarySearch @Inject constructor(private val dao: SearchDao) {
    private val matcher by lazy { InitialsMatcher(SortKeys.android()) }
    suspend fun tracks(query: String): List<TrackEntity> = withContext(Dispatchers.Default) {
        val q = query.trim()
        if (q.isEmpty()) return@withContext emptyList()
        if (q.all { it.isLetterOrDigit() && it.code < 128 })
            dao.tracks("%").filter { it.fileName.contains(q, ignoreCase = true) || matcher.matches(q, it.title, it.artistDisplay, it.album) }
        else dao.tracks("%" + MediaStoreSource.escapeLike(q) + "%")
    }
    suspend fun artists(query: String, limit: Int) = withContext(Dispatchers.Default) {
        dao.artists("%", Int.MAX_VALUE).filter { matcher.matches(query, it.name) }.take(limit)
    }
}
