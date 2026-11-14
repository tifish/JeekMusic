package com.tinyfish.jeekmusic.data.sync

/** Same content/presence three-way merge as Windows. Unknown is never interpreted as missing. */
internal object FileReconciliation {
    data class State(val known: Boolean,val hash: String?,val editedAtMs: Long=0)
    data class Baseline(val hash: String?,val version: String,val remoteHash: String?=null,val metadata: Boolean=false,val migration: Boolean=false)
    data class Decision(val action: String,val reason: String?=null)
    fun decide(base: Baseline?,local: State,remote: State,newestWins: Boolean=false): Decision {
        if(!local.known || !remote.known) return Decision("defer","state_unknown")
        val l=metadata(local.hash);val r=metadata(remote.hash)
        if(newestWins && l!=null && r!=null) {
            // Reuse the confirmed pair when the receiving filesystem rounded mtime.
            if(base?.metadata==true && !base.migration && local.hash==base.hash && remote.hash==base.remoteHash)return Decision("none")
            if(l.second!=r.second)return Decision(if(l.second>r.second)"upload" else "download","newer_file_mtime")
            if(l.first==r.first)return Decision(if(base!=null && local.hash==base.hash && remote.hash==(if(base.metadata)base.remoteHash else base.hash))"none" else "ack")
            if(local.editedAtMs!=remote.editedAtMs)return Decision(if(local.editedAtMs>remote.editedAtMs)"upload" else "download","newer_edit_time")
            return Decision("conflict","same_time_different_size")
        }
        if(base?.migration==true)return if(local.hash==remote.hash)Decision("ack") else Decision("conflict","legacy_metadata_unconfirmed")
        if(base==null) {
            if(local.hash==remote.hash) return Decision(if(local.hash==null) "none" else "ack")
            if(local.hash==null) return Decision("download")
            if(remote.hash==null) return Decision("upload")
            return Decision("conflict","initial_different_content")
        }
        val remoteBase=if(base.metadata)base.remoteHash else base.hash
        if(local.hash==base.hash && remote.hash==remoteBase)return Decision("none")
        if(local.hash==remote.hash)return Decision("ack")
        if(local.hash==base.hash) return Decision(if(remote.hash==null) "delete_local" else "download")
        if(remote.hash==remoteBase) return Decision(if(local.hash==null) "delete_remote" else "upload")
        return Decision("conflict",if(local.hash==null || remote.hash==null) "modified_vs_deleted" else "both_modified")
    }
    private fun metadata(version: String?): Pair<Long,Long>? {
        val parts=version?.split(':') ?: return null
        if(parts.size!=3 || parts[0]!="m2")return null
        val size=parts[1].toLongOrNull()?.takeIf { it>=0 } ?: return null
        return size to (parts[2].toLongOrNull() ?: return null)
    }
}
