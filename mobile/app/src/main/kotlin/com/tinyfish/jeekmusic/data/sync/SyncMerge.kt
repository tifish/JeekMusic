package com.tinyfish.jeekmusic.data.sync

/** 合并带来的一项本机改动（提示"已同步来自「设备名」的 N 项改动"用）。 */
data class SyncChange(val kind: String, val key: String, val deviceId: String, val deviceName: String)

/** 被同步覆盖或删除的本机播放列表（存进本机的同步历史，30 天内可以找回，需求 SY-5）。 */
data class ReplacedPlaylist(val old: SyncPlaylist, val deleted: Boolean, val deviceId: String, val deviceName: String)

data class MergeResult(val state: SyncState, val changes: List<SyncChange>, val replaced: List<ReplacedPlaylist>)

/**
 * 合并算法（需求 SY-3 ~ SY-6，desktop/docs/design.md §11.2）。纯函数，两端用同一份测试用例（shared/testcases/sync-merge.json）。
 *
 * - **每一项以最后修改的为准**：一个播放列表（名字和歌曲顺序作为一个整体）、一首歌一个维度的手动修正、一首歌的歌词偏移、
 *   整组曲库规则、整个导航顺序。时间相同时保留本机的。
 * - **删除记录**：播放列表只有在 updated_at 晚于删除时间时才存在（相同时删除优先），所以一边删掉的不会被另一边的旧数据加回来。
 *   删除记录、"改回自动"的修正、为 0 的偏移超过 [RETENTION_MS] 后清掉。
 * - **没有出现不等于删除**：别的设备的文件里没有某一项，不影响本机的这一项。
 * - 同一台设备在几个音乐文件夹里都有文件时，只用 written_at 最新的那份；本机自己的文件不参与。
 */
object SyncMerge {
    const val RETENTION_MS = 180L * 86_400_000

    fun normalizeReferences(state: SyncState, canonical: (String) -> String): SyncState {
        val tags = mutableMapOf<String, MutableMap<String, Stamped<String?>>>()
        for ((ref, dims) in state.tagOverrides.toSortedMap()) for ((dim, stamp) in dims) {
            val target = tags.getOrPut(canonical(ref)) { mutableMapOf() }
            if (target[dim]?.let { it.updatedAt >= stamp.updatedAt } != true) target[dim] = stamp
        }
        val offsets = mutableMapOf<String, Stamped<Long>>()
        for ((ref, stamp) in state.lyricsOffsets.toSortedMap()) {
            val key = canonical(ref)
            if (offsets[key]?.let { it.updatedAt >= stamp.updatedAt } != true) offsets[key] = stamp
        }
        return state.copy(playlists = state.playlists.mapValues { (_, p) -> p.copy(items = p.items.map(canonical)) },
            tagOverrides = tags, lyricsOffsets = offsets)
    }

    fun latestPerDevice(files: List<SyncFile>, selfId: String): List<SyncFile> =
        files.filter { it.device.id != selfId }
            .groupBy { it.device.id }
            .map { (_, list) -> list.maxBy { it.writtenAt } }
            .sortedBy { it.device.id }

    fun merge(local: SyncState, remoteFiles: List<SyncFile>, selfId: String, now: Long): MergeResult {
        val remotes = latestPerDevice(remoteFiles, selfId)
        val changes = mutableListOf<SyncChange>()
        val replaced = mutableListOf<ReplacedPlaylist>()
        fun change(kind: String, key: String, from: SyncFile) {
            changes += SyncChange(kind, key, from.device.id, from.device.name)
        }

        // 整组的：曲库规则、导航顺序
        var rules = local.rules
        var rulesFrom: SyncFile? = null
        var nav = local.nav
        var navFrom: SyncFile? = null
        for (f in remotes) {
            f.state.rules?.let { if (rules == null || it.updatedAt > rules!!.updatedAt) { rules = it; rulesFrom = f } }
            f.state.nav?.let { if (nav == null || it.updatedAt > nav!!.updatedAt) { nav = it; navFrom = f } }
        }
        rulesFrom?.let { if (rules?.value != local.rules?.value) change("rules", "", it) }
        navFrom?.let { if (nav?.value != local.nav?.value) change("nav", "", it) }

        // 播放列表和删除记录
        val playlists = mutableMapOf<String, SyncPlaylist>()
        val deleted = mutableMapOf<String, Long>()
        val uids = buildSet {
            addAll(local.playlists.keys)
            addAll(local.deletedPlaylists.keys)
            for (f in remotes) { addAll(f.state.playlists.keys); addAll(f.state.deletedPlaylists.keys) }
        }
        for (uid in uids.sorted()) {
            var best = local.playlists[uid]
            var bestFrom: SyncFile? = null
            var deletedAt = local.deletedPlaylists[uid]
            var deletedFrom: SyncFile? = null
            for (f in remotes) {
                f.state.playlists[uid]?.let { if (best == null || it.updatedAt > best!!.updatedAt) { best = it; bestFrom = f } }
                f.state.deletedPlaylists[uid]?.let { if (deletedAt == null || it > deletedAt!!) { deletedAt = it; deletedFrom = f } }
            }
            if (deletedAt != null && now - deletedAt!! > RETENTION_MS) deletedAt = null
            val alive = best != null && (deletedAt == null || best!!.updatedAt > deletedAt!!)
            val mine = local.playlists[uid]
            if (alive) {
                playlists[uid] = best!!
                val from = bestFrom
                if (from != null && (mine == null || mine.name != best!!.name || mine.items != best!!.items)) {
                    change("playlist", uid, from)
                    if (mine != null) replaced += ReplacedPlaylist(mine, deleted = false, from.device.id, from.device.name)
                }
            } else {
                deletedAt?.let { deleted[uid] = it }
                if (mine != null) {
                    val from = deletedFrom ?: remotes.firstOrNull()
                    if (from != null && deletedFrom != null) {
                        change("playlist", uid, from)
                        replaced += ReplacedPlaylist(mine, deleted = true, from.device.id, from.device.name)
                    }
                }
            }
        }

        // 手动修正：每首歌每个维度
        val overrides = mutableMapOf<String, MutableMap<String, Stamped<String?>>>()
        val overrideKeys = buildSet {
            for ((ref, dims) in local.tagOverrides) for (dim in dims.keys) add(ref to dim)
            for (f in remotes) for ((ref, dims) in f.state.tagOverrides) for (dim in dims.keys) add(ref to dim)
        }
        for ((ref, dim) in overrideKeys.sortedWith(compareBy({ it.first }, { it.second }))) {
            val mine = local.tagOverrides[ref]?.get(dim)
            var best = mine
            var from: SyncFile? = null
            for (f in remotes) f.state.tagOverrides[ref]?.get(dim)?.let { if (best == null || it.updatedAt > best!!.updatedAt) { best = it; from = f } }
            val b = best!!
            if (b.value == null && now - b.updatedAt > RETENTION_MS) continue
            overrides.getOrPut(ref) { mutableMapOf() }[dim] = b
            if (from != null && b.value != mine?.value) change("tag_override", "$ref|$dim", from!!)
        }

        // 歌词偏移
        val offsets = mutableMapOf<String, Stamped<Long>>()
        val offsetRefs = buildSet { addAll(local.lyricsOffsets.keys); for (f in remotes) addAll(f.state.lyricsOffsets.keys) }
        for (ref in offsetRefs.sorted()) {
            val mine = local.lyricsOffsets[ref]
            var best = mine
            var from: SyncFile? = null
            for (f in remotes) f.state.lyricsOffsets[ref]?.let { if (best == null || it.updatedAt > best!!.updatedAt) { best = it; from = f } }
            val b = best!!
            if (b.value == 0L && now - b.updatedAt > RETENTION_MS) continue
            offsets[ref] = b
            if (from != null && b.value != (mine?.value ?: 0L)) change("lyrics_offset", ref, from!!)
        }

        return MergeResult(
            SyncState(rules, nav, playlists, deleted, overrides, offsets),
            changes,
            replaced,
        )
    }
}
