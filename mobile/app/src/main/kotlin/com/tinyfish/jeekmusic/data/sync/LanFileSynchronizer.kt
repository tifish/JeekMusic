package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.util.Base64
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.UUID

internal class LanRemoteFileConflict(val reason: String): IOException(reason)
internal data class LanFileReport(val actions: MutableMap<String,Int> = mutableMapOf(),val conflicts: MutableList<JSONObject> = mutableListOf(),val deferred: MutableList<String> = mutableListOf(),var bytes: Long=0,var processed: Int=0,var conflictCount: Int=0,var deferredCount: Int=0) {
    fun defer(text: String) { deferredCount++;if(deferred.size<100)deferred+=text.take(2300) }
    fun conflict(row: JSONObject) { conflictCount++;if(conflicts.size<100)conflicts+=row }
}

/** One production implementation for initial sync, retries and explicit conflict choices.
 * Snapshots are paged to SQLite; only one file and bounded transport blocks enter memory. */
internal class LanFileSynchronizer(private val context: Context,private val db: LanFileDatabase,private val playing: PlayingFiles,private val localFactory: (JSONObject,Boolean)->LanLocalFiles={ mapping,pin -> LanLocalFiles.fromMapping(context,mapping,db,pin) }) {
    private val partials=File(context.filesDir,"lan-file-parts").apply { mkdirs() }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun hash(row: JSONObject?)=row?.optString("hash")?.takeUnless { row.isNull("hash") || it.isEmpty() }
    private fun state(row: JSONObject)=FileReconciliation.State(row.optBoolean("known",true),hash(row))
    private fun base(row: JSONObject?)=row?.takeIf { it.optString("version").isNotEmpty() }?.let { FileReconciliation.Baseline(hash(it),it.getString("version")) }
    private fun selected(path: String,selection: List<String>)=selection.any { it.isEmpty() || path==it || path.startsWith("$it/") }
    private fun checked(result: JSONObject): JSONObject {
        if(result.optBoolean("conflict"))throw LanRemoteFileConflict(result.optString("reason","concurrent_change"))
        if(result.optBoolean("deferred"))throw LanLocalDeferred(result.optString("reason","peer_deferred"))
        return result
    }
    private fun call(client: LanProtocol,scope: String,op: String,args: JSONObject=JSONObject())=checked(client.call(op,args.put("fileProtocol",1).put("scope",scope)))
    private fun change(baseline: FileReconciliation.Baseline?,value: FileReconciliation.State): String = when {
        !value.known->"unknown";baseline==null->if(value.hash==null) "absent" else "untracked_present"
        baseline.hash==value.hash->"unchanged";value.hash==null->"deleted";baseline.hash==null->"created";else->"modified"
    }
    private fun busy(mapping: JSONObject,path: String) {
        if(playing.isBusy(mapping.getString("volume")+":"+mapping.getString("relative")+path))throw LanLocalDeferred("playing_file")
    }
    private fun transaction(mapping: JSONObject,scope: String,path: String,kind: String,expected: String?,desired: String?,version: String,size: Long): JSONObject {
        val old=db.transaction(mapping.getString("key"),path,scope)
        if(old!=null && old.optString("kind")==kind && hash(old)==desired && (if(old.isNull("expectedHash"))null else old.optString("expectedHash"))==expected && old.optString("baselineVersion")==version && old.optString("state") in setOf("created","captured","uploading","prepared","old_moved","committed","remote_committed","vault_prepared","vault_old_moved","copying"))return old
        return JSONObject().put("operation",id()).put("mapping",mapping.getString("key")).put("scope",scope).put("path",path).put("kind",kind).put("expectedHash",expected ?: JSONObject.NULL).put("hash",desired ?: JSONObject.NULL).put("baselineVersion",version).put("size",size).put("state","created").also(db::transaction)
    }
    private fun store(row: JSONObject,state: String) { row.put("state",state).put("updatedAt",System.currentTimeMillis());db.transaction(row) }
    private fun privatePart(mapping: JSONObject,path: String,hash: String)=File(partials,LanProtocol.sha((mapping.getString("key")+"/"+path+"/"+hash).toByteArray())+".part")
    private fun cueReferences(part: File,path: String): List<String> {
        if(part.length()>2_000_000)throw LanLocalDeferred("cue_byte_budget")
        val text=Charsets.UTF_8.newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(part.readBytes())).toString()
        val declarations=Regex("""(?im)^\s*FILE\s+(?:"([^"]+)"|(\S+))\s+\S+\s*$""").findAll(text).toList()
        if(declarations.size !in 1..1000 || Regex("""(?im)^\s*FILE\b""").findAll(text).count()!=declarations.size)throw LanLocalDeferred("cue_invalid")
        return declarations.map { match ->
            val raw=match.groupValues[1].ifEmpty { match.groupValues[2] }.replace('\\','/')
            if(raw.startsWith('/') || raw.contains(':'))throw LanLocalDeferred("cue_path_invalid")
            val parts=path.substringBeforeLast('/',"").split('/').filter { it.isNotEmpty() }.toMutableList()
            for(segment in raw.split('/'))when(segment) { "","."->Unit;".."->if(parts.isEmpty())throw LanLocalDeferred("cue_path_invalid") else parts.removeAt(parts.lastIndex);else->parts+=segment }
            LanProtocol.relative(parts.joinToString("/"))
        }.distinct()
    }
    private fun privateBytes(): Long {
        val old=System.currentTimeMillis()-2*60*60*1000L
        var count=0;var size=0L;for(file in partials.listFiles().orEmpty()) { if(file.name.matches(Regex("[0-9a-f]{64}\\.part")) && file.lastModified()<old) { file.delete();continue };if(++count>4096)throw LanLocalDeferred("private_staging_count_budget");size=Math.addExact(size,file.length());if(size>16L*1024*1024*1024)throw LanLocalDeferred("private_staging_byte_budget") };return size
    }
    private suspend fun download(client: LanProtocol,scope: String,mapping: JSONObject,path: String,remote: JSONObject,plan: String,inside: Boolean,report: LanFileReport,progress: (String,LanFileReport)->Unit): File {
        val desired=remote.getString("hash");val size=remote.getLong("size");if(size<0 || size>8L*1024*1024*1024)throw LanLocalDeferred("file_byte_budget")
        val part=privatePart(mapping,path,desired)
        if(part.length()>size)check(part.delete())
        if(privateBytes()+size-part.length()>16L*1024*1024*1024 || partials.usableSpace<size-part.length()+256L*1024*1024)throw LanLocalDeferred("private_staging_byte_budget")
        FileOutputStream(part,true).use { output ->
            var offset=part.length()
            while(offset<size) {
                currentCoroutineContext().ensureActive()
                val args=JSONObject().put("path",path).put("offset",offset)
                val result=if(inside)call(client,scope,"file_read",args.put("size",size)) else client.call("read",args.put("rootId",mapping.getString("rootId")).put("plan",plan))
                val bytes=Base64.decode(result.getString("data"),Base64.NO_WRAP)
                require(bytes.isNotEmpty() && bytes.size<=262144 && offset+bytes.size<=size) { "Invalid file range" }
                output.write(bytes);offset+=bytes.size;report.bytes+=bytes.size;progress(path,report)
            }
            output.fd.sync()
        }
        if(com.tinyfish.jeekmusic.data.tags.SafeTagWriter.sha256(part)!=desired) { part.delete();throw LanLocalDeferred("download_integrity") }
        return part
    }
    private suspend fun upload(client: LanProtocol,scope: String,mapping: JSONObject,path: String,local: JSONObject,remoteHash: String?,baseline: JSONObject?,row: JSONObject,io: LanLocalFiles,resolve: Boolean,report: LanFileReport,progress: (String,LanFileReport)->Unit) {
        busy(mapping,path)
        val desired=local.getString("hash");val part=privatePart(mapping,path,desired)
        if(!part.exists() || com.tinyfish.jeekmusic.data.tags.SafeTagWriter.sha256(part)!=desired) {
            if(privateBytes()+local.getLong("size")>16L*1024*1024*1024 || partials.usableSpace<local.getLong("size")+256L*1024*1024)throw LanLocalDeferred("private_staging_byte_budget")
            io.capture(path,part,desired)
        }
        row.put("part",part.name);store(row,"captured")
        if(path.endsWith(".cue",true)) {
            val selection=mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } }
            val dependencies=cueReferences(part,path)
            for(reference in dependencies) {
                if(!selected(reference,selection) || db.held(mapping.getString("key"),reference))throw LanLocalDeferred("cue_dependency_outside_selection_or_conflicted")
                val here=io.observe(reference);val there=call(client,scope,"file_probe",JSONObject().put("path",reference))
                if(hash(here)==null || hash(here)!=hash(there))throw LanLocalDeferred("cue_dependency_not_synchronized")
            }
            local.put("dependencies",JSONArray(dependencies))
        }
        val begin=call(client,scope,"file_begin",JSONObject().put("operation",row.getString("operation")).put("path",path).put("hash",desired).put("size",part.length()).put("modifiedNs",local.optLong("modifiedNs")).put("expectedHash",remoteHash ?: JSONObject.NULL).put("baselineVersion",baseline?.optString("version").orEmpty()).put("resolve",resolve))
        var offset=begin.optLong("offset");require(offset in 0..part.length())
        if(!begin.optBoolean("committed")) {
            store(row,"uploading")
            part.inputStream().use { input ->
                var skipped=0L;while(skipped<offset) { val n=input.skip(offset-skipped);if(n<=0)throw IOException("Local upload source unavailable");skipped+=n }
                val buffer=ByteArray(262144)
                while(offset<part.length()) {
                    currentCoroutineContext().ensureActive();val n=input.read(buffer);if(n<=0)throw IOException("Local upload source unavailable")
                    val next=call(client,scope,"file_chunk",JSONObject().put("operation",row.getString("operation")).put("offset",offset).put("data",Base64.encodeToString(buffer,0,n,Base64.NO_WRAP)))
                    require(next.getLong("offset")==offset+n);offset+=n;report.bytes+=n;progress(path,report)
                }
            }
            call(client,scope,"file_finish",JSONObject().put("operation",row.getString("operation")))
        }
        store(row,"remote_committed")
    }
    private fun ack(client: LanProtocol,scope: String,mapping: JSONObject,path: String,desired: String?,baseline: JSONObject?,decision: String,row: JSONObject?): JSONObject {
        val request=JSONObject().put("path",path).put("hash",desired ?: JSONObject.NULL).put("localHash",desired ?: JSONObject.NULL).put("localKnown",true).put("baselineVersion",baseline?.optString("version").orEmpty()).put("operation",id()).put("decision",decision)
        if(row?.optString("kind") in setOf("upload","delete_remote"))request.put("mutationOperation",row!!.getString("operation"))
        val result=call(client,scope,"file_ack",request).put("path",path)
        db.baseline(mapping.getString("key"),result)
        if(row!=null) { db.confirmed(row);row.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { File(partials,it).delete() } }
        return result
    }
    private fun issue(mapping: JSONObject,scope: String,path: String,baseline: JSONObject?,effective: FileReconciliation.Baseline?,local: JSONObject,remote: JSONObject,reason: String,plan: String): JSONObject {
        val fingerprint=listOf(mapping.getString("key"),scope,path,baseline?.optString("version").orEmpty(),hash(local).orEmpty(),hash(remote).orEmpty()).joinToString("\n")
        return JSONObject().put("id",LanProtocol.sha(fingerprint.toByteArray())).put("mapping",mapping.getString("key")).put("scope",scope).put("rootId",mapping.getString("rootId")).put("path",path).put("baselineVersion",baseline?.optString("version").orEmpty()).put("localHash",hash(local) ?: JSONObject.NULL).put("remoteHash",hash(remote) ?: JSONObject.NULL).put("localChange",change(effective,state(local))).put("remoteChange",change(effective,state(remote))).put("reason",reason).put("plan",plan).also(db::issue)
    }
    suspend fun synchronize(mapping: JSONObject,client: LanProtocol,onPinned: ()->Unit,progress: (String,LanFileReport)->Unit,beforeChange: (String,JSONObject?,Boolean)->Unit,afterChange: suspend ()->Unit): LanFileReport {
        val report=LanFileReport();val key=mapping.getString("key")
        val capabilities=client.call("capabilities");require(capabilities.optInt("fileProtocol")==1) { "Upgrade both devices for bidirectional file sync" }
        val permissions=capabilities.getJSONObject("permissions")
        val scopeInfo=call(client,"","file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",key).put("selection",mapping.getJSONArray("selection")))
        val scope=scopeInfo.getString("scope");val revision=scopeInfo.getString("revision")
        val selection=mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } }
        db.resetSnapshots(key)
        var offset=0
        do {
            currentCoroutineContext().ensureActive();val page=call(client,scope,"file_rows",JSONObject().put("offset",offset).put("revision",revision));val rows=page.getJSONArray("rows")
            repeat(rows.length()) { db.baseline(key,rows.getJSONObject(it)) };offset=page.getInt("next")
        } while(page.optBoolean("hasMore"))
        val plan=client.call("plan",JSONObject().put("rootId",mapping.getString("rootId")).put("selection",mapping.getJSONArray("selection")))
        val planId=plan.getString("plan");offset=0
        while(offset<plan.getInt("total")) {
            currentCoroutineContext().ensureActive();val rows=client.call("manifest",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("offset",offset)).getJSONArray("files")
            require(rows.length()>0);repeat(rows.length()) { db.entry(key,"remote",rows.getJSONObject(it)) };offset+=rows.length()
        }
        client.close() // Hashing never occupies an authenticated network slot.
        localFactory(mapping,scopeInfo.getLong("total")==0L).use { io ->
            onPinned()
            var journalOffset=0
            while(true) {
                val rows=db.transactions(key,journalOffset);if(rows.isEmpty())break
                for(row in rows.filter { it.optString("scope")==scope && it.optString("kind") in setOf("download","delete_local") }) {
                    val part=row.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { File(partials,it) }
                    io.recover(row,part)?.let { report.defer(row.getString("path")+": "+it);db.hold(key,row.getString("path")) }
                }
                journalOffset+=rows.size
            }
            io.scan(selection,{ db.entry(key,"local",it) },{ report.defer("$it: local_directory_unknown") });db.requirePortableLayout(key)
            for(cues in listOf(false,true)) {
                offset=0
                while(true) {
                    val paths=db.paths(key,offset,cues);if(paths.isEmpty())break
                    for(path in paths) {
                        currentCoroutineContext().ensureActive();progress(path,report)
                        try {
                            if(db.held(key,path))throw LanLocalDeferred("local_recovery_required")
                            val inside=selected(path,selection)
                            var local=db.entry(key,"local",path)?.takeIf { it.optBoolean("known") } ?: io.observe(path)
                            var remote=db.entry(key,"remote",path)?.put("known",true) ?: if(inside)call(client,scope,"file_probe",JSONObject().put("path",path)) else JSONObject().put("known",false)
                            var baseline=db.baseline(key,path)
                            var pending=db.transaction(key,path,scope)
                            // Recover a committed transfer as causal history, without inventing a confirmed baseline.
                            if(pending?.optString("kind") in setOf("upload","delete_remote") && pending!!.optString("state") in setOf("captured","uploading","remote_committed")) {
                                val status=call(client,scope,"file_upload_status",JSONObject().put("operation",pending.getString("operation")))
                                if(status.optBoolean("committed"))store(pending,"remote_committed")
                                else if(pending.optString("kind")=="upload" && hash(pending)!=hash(local)) {
                                    if(status.optString("state")=="uploading" || status.optString("state")=="not_found") { if(status.optString("state")!="not_found")call(client,scope,"file_abort",JSONObject().put("operation",pending.getString("operation")));store(pending,"aborted");pending=null }
                                    else if(status.optString("state")=="prepared" && permissions.optBoolean("writeFiles")) { call(client,scope,"file_finish",JSONObject().put("operation",pending.getString("operation")));store(pending,"remote_committed") }
                                }
                            }
                            var effective=base(baseline)
                            if(pending!=null && pending.optString("state") in setOf("committed","remote_committed")) {
                                val committed=hash(pending)
                                remote=if(inside)call(client,scope,"file_probe",JSONObject().put("path",path)) else remote
                                if(inside && hash(remote)==committed && (baseline?.optString("version").orEmpty()==pending.optString("baselineVersion") || hash(baseline)==committed)) {
                                    baseline=if(hash(baseline)==committed && baseline!=null)baseline.also { db.confirmed(pending) } else ack(client,scope,mapping,path,committed,baseline,"ack",pending)
                                    effective=base(baseline);pending=null
                                } else effective=FileReconciliation.Baseline(committed,baseline?.optString("version").orEmpty())
                            }
                            var decision=if(inside)FileReconciliation.decide(effective,state(local),state(remote)) else when {
                                hash(local)==hash(remote)->FileReconciliation.Decision("none")
                                hash(local)==null->FileReconciliation.Decision("download")
                                else->FileReconciliation.Decision("conflict","initial_different_content")
                            }
                            var resolve=false
                            if(decision.action=="conflict") {
                                val conflict=issue(mapping,scope,path,baseline,effective,local,remote,decision.reason!!,planId)
                                val choice=db.issue(conflict.getString("id"))?.optString("choice")
                                if(choice in setOf("local","remote") && inside) {
                                    resolve=true;decision=if(choice=="local")FileReconciliation.Decision(if(hash(local)==null) "delete_remote" else "upload") else FileReconciliation.Decision(if(hash(remote)==null) "delete_local" else "download")
                                } else { report.conflict(conflict);if(!cues)db.hold(key,path);report.processed++;continue }
                            }
                            if(decision.action=="defer")throw LanLocalDeferred(decision.reason!!)
                            if(decision.action=="upload" && !permissions.optBoolean("writeFiles") || decision.action in setOf("delete_local","delete_remote") && !permissions.optBoolean("deleteFiles"))throw LanLocalDeferred("file_permission_required")
                            val version=baseline?.optString("version").orEmpty()
                            var transaction: JSONObject?=null
                            when(decision.action) {
                                "download" -> {
                                    busy(mapping,path)
                                    if(cues) {
                                        val dependencies=remote.optJSONArray("dependencies") ?: JSONArray()
                                        require((0 until dependencies.length()).none { db.held(key,dependencies.getString(it)) }) { "CUE dependency conflict; retained existing file" }
                                    }
                                    val part=download(client,scope,mapping,path,remote,planId,inside,report,progress)
                                    if(cues) {
                                        val dependencies=cueReferences(part,path)
                                        for(reference in dependencies) {
                                            val here=io.observe(reference);val there=if(selected(reference,selection))call(client,scope,"file_probe",JSONObject().put("path",reference)) else db.entry(key,"remote",reference)
                                            if(db.held(key,reference) || hash(here)==null || hash(here)!=hash(there))throw LanLocalDeferred("cue_dependency_not_synchronized")
                                        }
                                        remote.put("dependencies",JSONArray(dependencies))
                                    }
                                    transaction=transaction(mapping,scope,path,"download",hash(local),hash(remote),version,remote.getLong("size")).put("part",part.name).put("modifiedNs",remote.optLong("modifiedNs"))
                                    beforeChange(path,remote,false)
                                    io.install(path,part,transaction) {
                                        busy(mapping,path)
                                        if(inside) { val current=call(client,scope,"file_probe",JSONObject().put("path",path));if(hash(current)!=hash(remote))throw LanLocalDeferred("remote_changed_before_commit") }
                                        else client.call("read",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("path",path).put("offset",0))
                                    }
                                    local=io.observe(path)
                                }
                                "upload" -> {
                                    transaction=transaction(mapping,scope,path,"upload",hash(remote),hash(local),version,local.getLong("size"))
                                    upload(client,scope,mapping,path,local,hash(remote),baseline,transaction,io,resolve,report,progress)
                                    remote=call(client,scope,"file_probe",JSONObject().put("path",path))
                                    local.optJSONArray("dependencies")?.let { remote.put("dependencies",it) }
                                }
                                "delete_local" -> {
                                    busy(mapping,path);local=io.observe(path)
                                    transaction=transaction(mapping,scope,path,"delete_local",hash(local),null,version,0)
                                    beforeChange(path,null,true)
                                    io.delete(path,transaction) { busy(mapping,path);if(hash(call(client,scope,"file_probe",JSONObject().put("path",path)))!=null)throw LanLocalDeferred("remote_changed_before_commit") }
                                    local=JSONObject().put("known",true).put("hash",JSONObject.NULL)
                                }
                                "delete_remote" -> {
                                    if(hash(io.observe(path))!=null)throw LanLocalDeferred("local_changed_before_delete")
                                    transaction=transaction(mapping,scope,path,"delete_remote",hash(remote),null,version,0)
                                    call(client,scope,"file_delete",JSONObject().put("path",path).put("operation",transaction.getString("operation")).put("expectedHash",hash(remote)).put("baselineVersion",version).put("resolve",resolve));store(transaction,"remote_committed")
                                    remote=JSONObject().put("known",true).put("hash",JSONObject.NULL)
                                }
                            }
                            if(decision.action!="none" && inside) {
                                val desired=when(decision.action) { "upload"->hash(local);"delete_local","delete_remote"->null;else->hash(remote) }
                                ack(client,scope,mapping,path,desired,baseline,decision.action,transaction ?: pending)
                            } else if(!inside && transaction!=null) { db.confirmed(transaction);transaction.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { File(partials,it).delete() } }
                            if(decision.action!="none")report.actions[decision.action]=(report.actions[decision.action] ?: 0)+1
                            if(cues && hash(local)==hash(remote) && hash(local)!=null)beforeChange(path,remote,false)
                            afterChange();db.resolved(key,path)
                        } catch(e: kotlinx.coroutines.CancellationException) { throw e }
                        catch(e: LanRemoteFileConflict) { report.defer("$path: ${e.reason}; refresh the snapshot");if(!cues)db.hold(key,path) }
                        catch(e: LanLocalDeferred) { report.defer("$path: ${e.reason}");if(!cues)db.hold(key,path) }
                        catch(_: IOException) { report.defer("$path: peer_or_local_file_unavailable");if(!cues)db.hold(key,path) }
                        report.processed++
                    }
                    offset+=paths.size
                }
            }
        }
        return report
    }
    fun issues(mapping: String)=db.issues(mapping)
    fun choose(id: String,choice: String)=db.choose(id,choice)
}
