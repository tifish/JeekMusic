package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

/**
 * 多设备同步的数据模型和文件格式（需求 B-7，桌面端 SY-1 ~ SY-9；格式见 desktop/docs/design.md §11.1）。
 * 纯 Kotlin，不依赖 Android：合并算法和两端共用的测试用例（shared/testcases/sync-merge.json）都基于它。
 *
 * 所有歌曲引用都是"相对于音乐文件夹的路径"（`/` 分隔，CUE 虚拟歌曲加 `#NN`），时间都是 UTC 毫秒。
 */
data class Stamped<T>(val value: T, val updatedAt: Long)

/** 曲库规则（整组同步）。 */
data class SyncRules(
    val artistSeparators: List<String>,
    val artistExceptions: List<String>,
    val inferFromFileName: Boolean,
    val fileNameFormat: String,
    val legacyCharset: String?,
)

data class NavRef(val type: String, val ref: String?)

data class SyncPlaylist(val uid: String, val name: String, val updatedAt: Long, val items: List<String>)

/**
 * 一台设备的全部用户数据。手动修正的值为 null 表示"改回自动"（这条记录本身也要同步，告诉别的设备）；
 * 歌词偏移为 0 表示没有偏移。
 */
data class SyncState(
    val rules: Stamped<SyncRules>? = null,
    val nav: Stamped<List<NavRef>>? = null,
    val playlists: Map<String, SyncPlaylist> = emptyMap(),
    val deletedPlaylists: Map<String, Long> = emptyMap(),
    val tagOverrides: Map<String, Map<String, Stamped<String?>>> = emptyMap(),
    val lyricsOffsets: Map<String, Stamped<Long>> = emptyMap(),
)

data class SyncDevice(val id: String, val name: String, val platform: String, val appVersion: String)

/** 一个设备的同步文件（`.jeekmusic/sync/<设备 ID>.json`）。 */
data class SyncFile(val device: SyncDevice, val writtenAt: Long, val state: SyncState)

object SyncFormat {
    fun normalizeNavigation(items: List<NavRef>): List<NavRef> {
        val builtins = listOf("FOLDERS", "ARTISTS", "PLAYLISTS")
        val normalized = items.mapNotNull { item ->
            when (item.type) {
                "ARTIST" -> com.tinyfish.jeekmusic.data.scanner.ArtistSplitter.nameKey(item.ref.orEmpty())
                    .takeIf(String::isNotEmpty)?.let { NavRef("ARTIST", it) }
                in builtins -> NavRef(item.type, null)
                else -> null
            }
        }.distinct()
        return normalized + builtins.filter { type -> normalized.none { it.type == type } }.map { NavRef(it, null) }
    }

    const val FORMAT = 2
    const val DIR = "sync"
    const val LEGACY_DEVICE_ID = "legacy-backup"

    fun fileName(deviceId: String) = "$deviceId.json"

    fun featuresFileName(deviceId: String) = "$deviceId.features.json"

    // ── 写 ──

    fun toJson(file: SyncFile): JSONObject = JSONObject()
        .put("format", FORMAT)
        .put(
            "device",
            JSONObject().put("id", file.device.id).put("name", file.device.name)
                .put("platform", file.device.platform).put("app_version", file.device.appVersion),
        )
        .put("written_at", file.writtenAt)
        .also { stateToJson(file.state, it); splitExtendedTags(it) }

    // Older Android editors call enum.valueOf on every legacy dimension.
    // Keep new overrides in an ignored extension on the wire, unified internally.
    private fun splitExtendedTags(json: JSONObject) {
        val tags = json.getJSONObject("tag_overrides")
        val extra = JSONObject()
        for (ref in tags.keys().asSequence().toList()) {
            val dims = tags.getJSONObject(ref)
            for (dim in dims.keys().asSequence().toList()) if (dim !in listOf("MOOD", "VOICE", "GENRE")) {
                val target = extra.optJSONObject(ref) ?: JSONObject().also { extra.put(ref, it) }
                target.put(dim, dims.remove(dim))
            }
        }
        if (extra.length() > 0) json.put("extended_tag_overrides", extra)
    }

    private fun readTagOverrides(json: JSONObject): Map<String, Map<String, Stamped<String?>>> {
        val result = mutableMapOf<String, MutableMap<String, Stamped<String?>>>()
        for (key in listOf("tag_overrides", "extended_tag_overrides")) {
            if (!json.has(key) || json.isNull(key)) continue
            val map = json.getJSONObject(key)
            for (ref in map.keys()) {
                val dims = map.getJSONObject(ref)
                val target = result.getOrPut(ref) { mutableMapOf() }
                for (dim in dims.keys()) {
                    require(key != "extended_tag_overrides" || dim !in listOf("MOOD", "VOICE", "GENRE"))
                    val entry = dims.getJSONObject(dim)
                    require(entry.isNull("value") || entry.get("value") is String)
                    require(entry.get("updated_at") is Int || entry.get("updated_at") is Long)
                    val stamp = Stamped(entry.optStringOrNull("value"), entry.getLong("updated_at"))
                    require(stamp.updatedAt >= 0)
                    if (target[dim] == null || stamp.updatedAt > target.getValue(dim).updatedAt) target[dim] = stamp
                }
            }
        }
        return result
    }

    /** 把状态写进 [json]（同步文件和测试用例的"本机状态"共用这个结构）。 */
    fun stateToJson(state: SyncState, json: JSONObject = JSONObject()): JSONObject {
        state.rules?.let { r ->
            json.put("rules", JSONObject().put("updated_at", r.updatedAt).put("value", rulesToJson(r.value)))
        }
        state.nav?.let { n ->
            json.put(
                "nav",
                JSONObject().put("updated_at", n.updatedAt)
                    .put("items", JSONArray(n.value.map { JSONObject().put("type", it.type).put("ref", it.ref ?: JSONObject.NULL) })),
            )
        }
        json.put(
            "playlists",
            JSONArray(
                state.playlists.values.sortedBy { it.uid }.map { p ->
                    JSONObject().put("uid", p.uid).put("name", p.name).put("updated_at", p.updatedAt).put("items", JSONArray(p.items))
                },
            ),
        )
        json.put(
            "deleted_playlists",
            JSONArray(state.deletedPlaylists.entries.sortedBy { it.key }.map { JSONObject().put("uid", it.key).put("deleted_at", it.value) }),
        )
        json.put(
            "tag_overrides",
            JSONObject().also { o ->
                for ((ref, dims) in state.tagOverrides.toSortedMap()) {
                    o.put(ref, JSONObject().also { d -> for ((dim, s) in dims.toSortedMap()) d.put(dim, JSONObject().put("value", s.value ?: JSONObject.NULL).put("updated_at", s.updatedAt)) })
                }
            },
        )
        json.put(
            "lyrics_offsets",
            JSONObject().also { o ->
                for ((ref, s) in state.lyricsOffsets.toSortedMap()) o.put(ref, JSONObject().put("offset_ms", s.value).put("updated_at", s.updatedAt))
            },
        )
        return json
    }

    fun rulesToJson(r: SyncRules): JSONObject = JSONObject()
        .put("artist_separators", JSONArray(r.artistSeparators))
        .put("artist_exceptions", JSONArray(r.artistExceptions))
        .put("infer_from_file_name", r.inferFromFileName)
        .put("file_name_format", r.fileNameFormat)
        .put("legacy_charset", r.legacyCharset ?: JSONObject.NULL)

    // ── 读 ──

    /** 解析同步文件；格式不对时返回 null（同步工具写到一半等，跳过等下一次）。 */
    fun parse(text: String): SyncFile? = runCatching {
        val json = JSONObject(text)
        if (json.optInt("format") != FORMAT) return null
        val d = json.getJSONObject("device")
        SyncFile(
            SyncDevice(d.getString("id"), d.optString("name"), d.optString("platform"), d.optString("app_version")),
            json.getLong("written_at"),
            stateFromJson(json),
        )
    }.getOrNull()

    fun stateFromJson(json: JSONObject): SyncState = SyncState(
        rules = json.optJSONObject("rules")?.let { Stamped(rulesFromJson(it.getJSONObject("value")), it.getLong("updated_at")) },
        nav = json.optJSONObject("nav")?.let { n ->
            Stamped(n.getJSONArray("items").objects().map { NavRef(it.getString("type"), it.optStringOrNull("ref")?.takeIf { r -> r.isNotEmpty() }) }, n.getLong("updated_at"))
        },
        playlists = json.optJSONArray("playlists").objects().map { p ->
            SyncPlaylist(p.getString("uid"), p.getString("name"), p.getLong("updated_at"), p.optJSONArray("items").strings())
        }.associateBy { it.uid },
        deletedPlaylists = json.optJSONArray("deleted_playlists").objects().associate { it.getString("uid") to it.getLong("deleted_at") },
        tagOverrides = readTagOverrides(json),
        lyricsOffsets = json.optJSONObject("lyrics_offsets")?.let { o ->
            o.keys().asSequence().associateWith { ref -> o.getJSONObject(ref).let { Stamped(it.getLong("offset_ms"), it.getLong("updated_at")) } }
        }.orEmpty(),
    )

    fun rulesFromJson(v: JSONObject) = SyncRules(
        artistSeparators = v.optJSONArray("artist_separators").strings(),
        artistExceptions = v.optJSONArray("artist_exceptions").strings(),
        inferFromFileName = v.optBoolean("infer_from_file_name", true),
        fileNameFormat = v.optString("file_name_format", "ARTIST_TITLE"),
        legacyCharset = v.optStringOrNull("legacy_charset"),
    )

    /**
     * 移动端 1.0 写的 `.jeekmusic/backup.json`（format 1）当作一台"旧设备"读入（需求 SY-7）。
     * 旧播放列表没有 uid，用 [legacyUid]；时间戳都是 1（比"从没设置过"的 0 新，比任何真实修改都旧）。
     */
    fun parseLegacy(text: String): SyncFile? = runCatching {
        val json = JSONObject(text)
        if (json.optInt("format") != 1) return null
        val settings = json.optJSONObject("settings")
        val rules = settings?.let {
            SyncRules(
                artistSeparators = it.optJSONArray("artist_separators").strings().sorted(),
                artistExceptions = it.optJSONArray("artist_exceptions").strings(),
                inferFromFileName = it.optBoolean("infer_from_file_name", true),
                fileNameFormat = it.optString("file_name_format", "ARTIST_TITLE"),
                legacyCharset = it.optStringOrNull("legacy_charset"),
            )
        }
        val nav = json.optJSONArray("pinned_tabs")?.objects()?.map { NavRef(it.getString("type"), it.optStringOrNull("ref")?.takeIf { r -> r.isNotEmpty() }) }
        val playlists = json.optJSONArray("playlists").objects().map { p ->
            val name = p.getString("name")
            SyncPlaylist(legacyUid(name), name, LEGACY_STAMP, p.optJSONArray("items").strings())
        }
        SyncFile(
            SyncDevice(LEGACY_DEVICE_ID, "旧版备份", "android", json.optString("app_version")),
            LEGACY_STAMP,
            SyncState(
                rules = rules?.let { Stamped(it, LEGACY_STAMP) },
                nav = nav?.takeIf { it.isNotEmpty() }?.let { Stamped(it.filter { n -> n.type != "MOOD" }, LEGACY_STAMP) },
                playlists = playlists.associateBy { it.uid },
            ),
        )
    }.getOrNull()

    /** 旧备份里的播放列表没有 uid："legacy-" + 名字的 SHA-1（UTF-8，十六进制小写），两端算出来一样（设计 §11.5）。 */
    fun legacyUid(name: String): String =
        "legacy-" + MessageDigest.getInstance("SHA-1").digest(name.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }

    const val LEGACY_STAMP = 1L

    private fun JSONArray?.objects(): List<JSONObject> = if (this == null) emptyList() else List(length()) { getJSONObject(it) }
    private fun JSONArray?.strings(): List<String> = if (this == null) emptyList() else List(length()) { getString(it) }
    private fun JSONObject.optStringOrNull(key: String): String? = if (!has(key) || isNull(key)) null else getString(key)
}
