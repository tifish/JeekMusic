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
internal data class LanFileReport(val actions: MutableMap<String,Int> = mutableMapOf(),val conflicts: MutableList<JSONObject> = mutableListOf(),val deferred: MutableList<String> = mutableListOf(),var bytes: Long=0,var processed: Int=0,var conflictCount: Int=0,var deferredCount: Int=0,var metadataObservations: Long=0,var hashBytes: Long=0) {
    fun defer(text: String) { deferredCount++;if(deferred.size<100)deferred+=text.take(2300) }
    fun conflict(row: JSONObject) { conflictCount++;if(conflicts.size<100)conflicts+=row }
}

/** One production implementation for initial sync, retries and explicit conflict choices.
 * Snapshots are paged to SQLite; only one file and bounded transport blocks enter memory. */
internal class LanFileSynchronizer(private val context: Context,private val db: LanFileDatabase,private val playing: PlayingFiles,private val localFactory: (JSONObject,Boolean)->LanLocalFiles={ mapping,pin -> LanLocalFiles.fromMapping(context,mapping,db,pin) }) {
    private val partials=File(context.filesDir,"lan-file-parts").apply { mkdirs() }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun hash(row: JSONObject?)=row?.optString("hash")?.takeUnless { row.isNull("hash") || it.isEmpty() }
    private fun meta(row: JSONObject?)=LanFileMetrics.version(row)
    private fun state(row: JSONObject)=FileReconciliation.State(row.optBoolean("known",true),meta(row))
    private fun base(row: JSONObject?)=row?.takeIf { it.optString("version").isNotEmpty() }?.let { FileReconciliation.Baseline(it.optString("localVersion").takeUnless { _ -> it.isNull("localVersion") },it.getString("version"),it.optString("remoteVersion").takeUnless { _ -> it.isNull("remoteVersion") },true,it.optString("migration")=="legacy_metadata_unconfirmed") }
    private fun selected(path: String,selection: List<String>)=selection.any { it.isEmpty() || path==it || path.startsWith("$it/") }
    private fun checked(result: JSONObject): JSONObject {
        if(result.optBoolean("conflict"))throw LanRemoteFileConflict(result.optString("reason","concurrent_change"))
        if(result.optBoolean("deferred"))throw LanLocalDeferred(result.optString("reason","peer_deferred"))
        return result
    }
    private fun call(client: LanProtocol,scope: String,op: String,args: JSONObject=JSONObject())=checked(client.call(op,args.put("fileProtocol",2).put("scope",scope)))
    private fun change(baseline: FileReconciliation.Baseline?,value: FileReconciliation.State): String = when {
        !value.known->"unknown";baseline==null->if(value.hash==null) "absent" else "untracked_present"
        baseline.hash==value.hash->"unchanged";value.hash==null->"deleted";baseline.hash==null->"created";else->"modified"
    }
    private fun busy(mapping: JSONObject,path: String) {
        if(playing.isBusy(mapping.getString("volume")+":"+mapping.getString("relative")+path))throw LanLocalDeferred("playing_file")
    }
    private fun transaction(mapping: JSONObject,scope: String,path: String,kind: String,expected: String?,desired: String?,version: String,size: Long): JSONObject {
        val old=db.transaction(mapping.getString("key"),path,scope)
        if(old!=null && old.optString("kind")==kind && hash(old)==desired && (if(old.isNull("expectedHash"))null else old.optString("expectedHash"))==expected && old.optString("baselineVersion")==version && old.optString("state") in setOf("created","captured","uploading","prepared","old_moved","vault_prepared","vault_old_moved","copying"))return old
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
                val result=if(inside)call(client,scope,"file_read",args.put("size",size).put("sourceVersion",meta(remote))) else client.call("read",args.put("rootId",mapping.getString("rootId")).put("plan",plan))
                val bytes=Base64.decode(result.getString("data"),Base64.NO_WRAP)
                require(bytes.isNotEmpty() && bytes.size<=262144 && offset+bytes.size<=size) { "Invalid file range" }
                output.write(bytes);offset+=bytes.size;report.bytes+=bytes.size;progress(path,report)
            }
            output.fd.sync()
        }
        if(LanFileMetrics.digest(part)!=desired) { part.delete();throw LanLocalDeferred("download_integrity") }
        return part
    }
    private suspend fun upload(client: LanProtocol,scope: String,mapping: JSONObject,path: String,local: JSONObject,remoteHash: String?,baseline: JSONObject?,row: JSONObject,io: LanLocalFiles,resolve: Boolean,report: LanFileReport,progress: (String,LanFileReport)->Unit) {
        busy(mapping,path)
        val desired=local.getString("hash");val part=privatePart(mapping,path,desired)
        if(!part.exists() || LanFileMetrics.digest(part)!=desired) {
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
                if(meta(here)==null || !equivalent(mapping,reference,here,there))throw LanLocalDeferred("cue_dependency_not_synchronized")
            }
            local.put("dependencies",JSONArray(dependencies))
        }
        val begin=call(client,scope,"file_begin",JSONObject().put("operation",row.getString("operation")).put("path",path).put("hash",desired).put("size",part.length()).put("modifiedNs",local.optLong("modifiedNs")).put("expectedHash",remoteHash ?: JSONObject.NULL).put("expectedVersion",row.opt("expectedVersion") ?: JSONObject.NULL).put("baselineVersion",baseline?.optString("version").orEmpty()).put("resolve",resolve))
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
        val receipt=call(client,scope,"file_upload_status",JSONObject().put("operation",row.getString("operation")))
        row.put("localVersion",row.getString("sourceVersion")).put("remoteVersion",receipt.opt("resultVersion") ?: JSONObject.NULL);store(row,"remote_committed")
    }
    private fun equivalent(mapping: JSONObject,path: String,local: JSONObject,remote: JSONObject?): Boolean {
        if(meta(local)==meta(remote))return true
        val baseline=base(db.baseline(mapping.getString("key"),path)) ?: return false
        return !baseline.migration && meta(local)==baseline.hash && meta(remote)==baseline.remoteHash
    }
    private fun ack(client: LanProtocol,scope: String,mapping: JSONObject,path: String,local: JSONObject,remote: JSONObject,baseline: JSONObject?,decision: String,row: JSONObject?): JSONObject {
        val request=JSONObject().put("path",path).put("remoteVersion",meta(remote) ?: JSONObject.NULL).put("localVersion",meta(local) ?: JSONObject.NULL).put("localPrecisionNs",local.optLong("precisionNs",1)).put("localKnown",local.optBoolean("known"))
            .put("baselineVersion",baseline?.optString("version").orEmpty()).put("operation",row?.optString("ackOperation")?.takeIf { it.isNotEmpty() } ?: id()).put("decision",decision).put("hash",hash(row) ?: JSONObject.NULL)
        if(row!=null) { row.put("ackOperation",request.getString("operation")).put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL);db.transaction(row) }
        if(row?.optString("kind") in setOf("upload","delete_remote"))request.put("mutationOperation",row!!.getString("operation"))
        val result=call(client,scope,"file_ack",request).put("path",path)
        db.baseline(mapping.getString("key"),result)
        if(row!=null) { db.confirmed(row);row.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { File(partials,it).delete() } }
        return result
    }
    private fun issue(mapping: JSONObject,scope: String,path: String,baseline: JSONObject?,effective: FileReconciliation.Baseline?,local: JSONObject,remote: JSONObject,reason: String,plan: String): JSONObject {
        val fingerprint=listOf(mapping.getString("key"),scope,path,baseline?.optString("version").orEmpty(),meta(local).orEmpty(),meta(remote).orEmpty()).joinToString("\n")
        return JSONObject().put("id",LanProtocol.sha(fingerprint.toByteArray())).put("mapping",mapping.getString("key")).put("scope",scope).put("rootId",mapping.getString("rootId")).put("path",path).put("baselineVersion",baseline?.optString("version").orEmpty()).put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL).put("localChange",change(effective,state(local))).put("remoteChange",change(effective,state(remote))).put("reason",reason).put("plan",plan).also(db::issue)
    }
    suspend fun synchronize(mapping: JSONObject,client: LanProtocol,onPinned: ()->Unit,progress: (String,LanFileReport)->Unit,beforeChange: (String,JSONObject?,Boolean)->Unit,afterChange: suspend ()->Unit): LanFileReport {
        val report=LanFileReport();val key=mapping.getString("key")
        val startMetadata=LanFileMetrics.metadataObservations.get();val startHash=LanFileMetrics.hashBytes.get()
        val capabilities=client.call("capabilities");require(capabilities.optInt("fileProtocol")==2) { "Upgrade both devices for size+mtime file sync" }
        LanProtocol.requirePairedCapabilities(capabilities)
        val scopeInfo=call(client,"","file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",key).put("selection",mapping.getJSONArray("selection")))
        val scope=scopeInfo.getString("scope");val revision=scopeInfo.getString("revision")
        val selection=mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } }
        db.resetSnapshots(key)
        var offset=0
        do {
            currentCoroutineContext().ensureActive();val page=call(client,scope,"file_rows",JSONObject().put("offset",offset).put("revision",revision));val rows=page.getJSONArray("rows")
            repeat(rows.length()) { db.baseline(key,rows.getJSONObject(it)) };offset=page.getInt("next")
        } while(page.optBoolean("hasMore"))
        val plan=client.call("plan",JSONObject().put("fileProtocol",2).put("rootId",mapping.getString("rootId")).put("selection",mapping.getJSONArray("selection")))
        val planId=plan.getString("plan");offset=0
        while(offset<plan.getInt("total")) {
            currentCoroutineContext().ensureActive();val rows=client.call("manifest",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("offset",offset)).getJSONArray("files")
            require(rows.length()>0);repeat(rows.length()) { db.entry(key,"remote",rows.getJSONObject(it)) };offset+=rows.length()
        }
        client.close()
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
                            if(pending?.optString("kind") in setOf("upload","delete_remote") && pending!!.optString("state") in setOf("captured","uploading","remote_committed")) {
                                val status=call(client,scope,"file_upload_status",JSONObject().put("operation",pending.getString("operation")))
                                if(status.optBoolean("committed")) { if(pending.optString("kind")=="upload" && pending.has("sourceVersion"))pending.put("localVersion",pending.getString("sourceVersion")).put("remoteVersion",status.opt("resultVersion") ?: JSONObject.NULL);if(pending.optString("kind")=="delete_remote")pending.put("localVersion",JSONObject.NULL).put("remoteVersion",JSONObject.NULL);store(pending,"remote_committed") }
                                else if(pending.optString("kind")=="upload" && pending.optString("sourceVersion")!=meta(local)) {
                                    if(status.optString("state") in setOf("uploading","not_found")) { if(status.optString("state")!="not_found")call(client,scope,"file_abort",JSONObject().put("operation",pending.getString("operation")));store(pending,"aborted");pending=null }
                                    else if(status.optString("state")=="prepared") { call(client,scope,"file_finish",JSONObject().put("operation",pending.getString("operation")));store(pending,"remote_committed") }
                                }
                            }
                            var effective=base(baseline)
                            if(inside && pending!=null && pending.optString("state") in setOf("committed","remote_committed")) {
                                remote=call(client,scope,"file_probe",JSONObject().put("path",path))
                                // The exact landed observations are journaled before acknowledgement, including rounding.
                                // If the process died earlier, recover only this operation's content and source version.
                                if(!pending.has("localVersion") || !pending.has("remoteVersion")) {
                                    val desired=hash(pending)
                                    val verifiedRemote=call(client,scope,"file_verify",JSONObject().put("path",path).put("expectedVersion",meta(remote) ?: JSONObject.NULL))
                                    val verifiedLocal=io.verify(path,meta(local))
                                    val source=pending.optString("sourceVersion")
                                    val sourceUnchanged=when(pending.optString("kind")) { "upload"->meta(local)==source;"download"->meta(remote)==source;else->true }
                                    if(hash(verifiedRemote)==desired && hash(verifiedLocal)==desired && sourceUnchanged)pending.put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL).also(db::transaction)
                                    else throw LanLocalDeferred("committed_operation_needs_reconciliation")
                                }
                                val landedLocal=if(pending.isNull("localVersion"))null else pending.getString("localVersion")
                                val landedRemote=if(pending.isNull("remoteVersion"))null else pending.getString("remoteVersion")
                                if(baseline?.optString("comparisonProtocol")=="2" && base(baseline)?.let { it.hash==landedLocal && it.remoteHash==landedRemote }==true) { db.confirmed(pending);pending=null }
                                else if(meta(remote)==landedRemote) {
                                    // A durable successful landing is causal evidence even if the phone
                                    // has since been edited. Confirm that historical pair first, then
                                    // compare the current local version against it; never lose the edit.
                                    val landed=JSONObject().put("known",true).put("metadataVersion",landedLocal ?: JSONObject.NULL).put("precisionNs",1)
                                    baseline=ack(client,scope,mapping,path,landed,remote,baseline,"ack",pending);effective=base(baseline);pending=null
                                }
                                else effective=FileReconciliation.Baseline(landedLocal,baseline?.optString("version").orEmpty(),landedRemote,true)
                            }
                            var decision=if(inside)FileReconciliation.decide(effective,state(local),state(remote)) else when {
                                equivalent(mapping,path,local,remote)->FileReconciliation.Decision("none")
                                meta(local)==null->FileReconciliation.Decision("download")
                                else->FileReconciliation.Decision("conflict","initial_different_content")
                            }
                            var resolve=false
                            if(decision.action=="conflict") {
                                val conflict=issue(mapping,scope,path,baseline,effective,local,remote,decision.reason!!,planId)
                                val choice=db.issue(conflict.getString("id"))?.optString("choice")
                                if(choice in setOf("local","remote") && inside) { resolve=true;decision=if(choice=="local")FileReconciliation.Decision(if(meta(local)==null) "delete_remote" else "upload") else FileReconciliation.Decision(if(meta(remote)==null) "delete_local" else "download") }
                                else { report.conflict(conflict);if(!cues)db.hold(key,path);report.processed++;continue }
                            }
                            if(decision.action=="defer")throw LanLocalDeferred(decision.reason!!)
                            val version=baseline?.optString("version").orEmpty()
                            var transaction: JSONObject?=null
                            if(decision.action in setOf("download","upload","delete_local","delete_remote")) {
                                busy(mapping,path)
                                kotlinx.coroutines.delay(500)
                                local=io.verify(path,meta(local))
                                remote=if(inside)call(client,scope,"file_verify",JSONObject().put("path",path).put("expectedVersion",meta(remote) ?: JSONObject.NULL)) else client.call("verify",JSONObject().put("fileProtocol",2).put("rootId",mapping.getString("rootId")).put("plan",planId).put("path",path).put("expectedVersion",meta(remote)))
                            }
                            when(decision.action) {
                                "download" -> {
                                    val part=download(client,scope,mapping,path,remote,planId,inside,report,progress)
                                    if(cues) {
                                        val dependencies=cueReferences(part,path)
                                        for(reference in dependencies) {
                                            val here=io.observe(reference);val there=if(selected(reference,selection))call(client,scope,"file_probe",JSONObject().put("path",reference)) else db.entry(key,"remote",reference)
                                            if(db.held(key,reference) || meta(here)==null || !equivalent(mapping,reference,here,there))throw LanLocalDeferred("cue_dependency_not_synchronized")
                                        }
                                        remote.put("dependencies",JSONArray(dependencies))
                                    }
                                    transaction=transaction(mapping,scope,path,"download",hash(local),hash(remote),version,remote.getLong("size")).put("part",part.name).put("modifiedNs",remote.getLong("modifiedNs")).put("sourceVersion",meta(remote)).put("expectedVersion",meta(local) ?: JSONObject.NULL)
                                    store(transaction,"created");beforeChange(path,remote,false)
                                    io.install(path,part,transaction) {
                                        busy(mapping,path)
                                        if(inside) { val current=call(client,scope,"file_probe",JSONObject().put("path",path));if(meta(current)!=meta(remote))throw LanLocalDeferred("remote_changed_before_commit") } else client.call("read",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("path",path).put("offset",0))
                                    }
                                    local=io.observe(path)
                                }
                                "upload" -> {
                                    transaction=transaction(mapping,scope,path,"upload",hash(remote),hash(local),version,local.getLong("size")).put("sourceVersion",meta(local)).put("expectedVersion",meta(remote) ?: JSONObject.NULL)
                                    store(transaction,"created")
                                    upload(client,scope,mapping,path,local,hash(remote),baseline,transaction,io,resolve,report,progress)
                                    remote=call(client,scope,"file_probe",JSONObject().put("path",path))
                                    if(meta(io.observe(path))!=meta(local))throw LanLocalDeferred("local_changed_during_upload")
                                }
                                "delete_local" -> {
                                    transaction=transaction(mapping,scope,path,"delete_local",hash(local),null,version,0).put("expectedVersion",meta(local) ?: JSONObject.NULL);beforeChange(path,null,true)
                                    io.delete(path,transaction) { busy(mapping,path);if(meta(call(client,scope,"file_probe",JSONObject().put("path",path)))!=null)throw LanLocalDeferred("remote_changed_before_commit") }
                                    local=LanFileMetrics.absent()
                                }
                                "delete_remote" -> {
                                    if(meta(io.observe(path))!=null)throw LanLocalDeferred("local_changed_before_delete")
                                    transaction=transaction(mapping,scope,path,"delete_remote",hash(remote),null,version,0)
                                    call(client,scope,"file_delete",JSONObject().put("path",path).put("operation",transaction.getString("operation")).put("expectedHash",hash(remote)).put("expectedVersion",meta(remote)).put("baselineVersion",version).put("resolve",resolve));transaction.put("localVersion",JSONObject.NULL).put("remoteVersion",JSONObject.NULL);store(transaction,"remote_committed")
                                    remote=LanFileMetrics.absent()
                                }
                            }
                            if(decision.action!="none") {
                                // Recheck both metadata snapshots after all transfer I/O, before persisting the mapping.
                                if(meta(io.observe(path))!=meta(local))throw LanLocalDeferred("local_changed_before_ack")
                                if(inside)ack(client,scope,mapping,path,local,remote,baseline,decision.action,transaction ?: pending) else { db.dependencyBaseline(key,path,meta(local)!!,meta(remote)!!);if(transaction!=null)db.confirmed(transaction) }
                                report.actions[decision.action]=(report.actions[decision.action] ?: 0)+1
                            }
                            if(cues && decision.action!="none" && meta(local)!=null)beforeChange(path,remote,false)
                            if(decision.action!="none")afterChange()
                            db.resolved(key,path)
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
        report.metadataObservations=LanFileMetrics.metadataObservations.get()-startMetadata;report.hashBytes=LanFileMetrics.hashBytes.get()-startHash
        return report
    }
    fun issues(mapping: String)=db.issues(mapping)
    fun choose(id: String,choice: String)=db.choose(id,choice)
}
