package com.tinyfish.jeekmusic.data.sync

/** Same content/presence three-way merge as Windows. Unknown is never interpreted as missing. */
internal object FileReconciliation {
    data class State(val known: Boolean,val hash: String?)
    data class Baseline(val hash: String?,val version: String)
    data class Decision(val action: String,val reason: String?=null)
    fun decide(base: Baseline?,local: State,remote: State): Decision {
        if(!local.known || !remote.known) return Decision("defer","state_unknown")
        if(base==null) {
            if(local.hash==remote.hash) return Decision(if(local.hash==null) "none" else "ack")
            if(local.hash==null) return Decision("download")
            if(remote.hash==null) return Decision("upload")
            return Decision("conflict","initial_different_content")
        }
        if(local.hash==remote.hash) return Decision(if(local.hash==base.hash) "none" else "ack")
        if(local.hash==base.hash) return Decision(if(remote.hash==null) "delete_local" else "download")
        if(remote.hash==base.hash) return Decision(if(local.hash==null) "delete_remote" else "upload")
        return Decision("conflict",if(local.hash==null || remote.hash==null) "modified_vs_deleted" else "both_modified")
    }
}
