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
internal data class LanFileReport(val actions: MutableMap<String,Int> = mutableMapOf(),val conflicts: MutableList<JSONObject> = mutableListOf(),val deferred: MutableList<String> = mutableListOf(),var bytes: Long=0,var processed: Int=0,var conflictCount: Int=0,var deferredCount: Int=0,var metadataObservations: Long=0,var hashBytes: Long=0,var preparationMillis: Long=-1,var firstTransferMillis: Long=-1,var protocolRequests: Long=0,var manifestCacheHit: Boolean=false,var metadataRows: Int=0,var incremental: Boolean=false,var canReuseAudio: Boolean=false,var reusedBytes: Long=0,var reusedFiles: Int=0,var localIndexHit: Boolean=false,var localIndexRefreshed: Int=0,var libraryMetadataReused: Int=0) {
    fun defer(text: String) { deferredCount++;if(deferred.size<100)deferred+=text.take(2300) }
    fun conflict(row: JSONObject) { conflictCount++;if(conflicts.size<100)conflicts+=row }
}

/** One production implementation for initial sync, retries and explicit conflict choices.
 * Snapshots are paged to SQLite; only one file and bounded transport blocks enter memory. */
internal class LanFileSynchronizer(private val context: Context,private val db: LanFileDatabase,private val playing: PlayingFiles,private val fileIndex: com.tinyfish.jeekmusic.data.scanner.LocalFileIndex?=null,private val localFactory: (JSONObject,Boolean)->LanLocalFiles={ mapping,pin -> LanLocalFiles.fromMapping(context,mapping,db,pin) }) {
    private val partials=File(context.filesDir,"lan-file-parts").apply { mkdirs() }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun stamp(row: JSONObject?)=row?.optString("stamp")?.takeUnless { row.isNull("stamp") || it.isEmpty() }
    private fun meta(row: JSONObject?)=LanFileMetrics.version(row)
    private fun state(row: JSONObject)=FileReconciliation.State(row.optBoolean("known",true),meta(row),row.optLong("editedAtMs"))
    private fun base(row: JSONObject?)=row?.takeIf { it.optString("version").isNotEmpty() }?.let { FileReconciliation.Baseline(it.optString("localVersion").takeUnless { _ -> it.isNull("localVersion") },it.getString("version"),it.optString("remoteVersion").takeUnless { _ -> it.isNull("remoteVersion") },true,it.optString("migration")=="legacy_metadata_unconfirmed") }
    private fun selected(path: String,selection: List<String>)=selection.any { it.isEmpty() || path==it || path.startsWith("$it/") }
    private fun checked(result: JSONObject): JSONObject {
        if(result.optBoolean("conflict"))throw LanRemoteFileConflict(result.optString("reason","concurrent_change"))
        if(result.optBoolean("deferred"))throw LanLocalDeferred(result.optString("reason","peer_deferred"))
        return result
    }
    private fun call(client: LanProtocol,scope: String,op: String,args: JSONObject=JSONObject())=checked(client.call(op,args.put("fileProtocol",3).put("scope",scope)))
    private fun change(baseline: FileReconciliation.Baseline?,value: FileReconciliation.State): String = when {
        !value.known->"unknown";baseline==null->if(value.hash==null) "absent" else "untracked_present"
        baseline.hash==value.hash->"unchanged";value.hash==null->"deleted";baseline.hash==null->"created";else->"modified"
    }
    private fun busy(mapping: JSONObject,path: String) {
        if(playing.isBusy(mapping.getString("volume")+":"+mapping.getString("relative")+path))throw LanLocalDeferred("playing_file")
    }
    private fun transaction(mapping: JSONObject,scope: String,path: String,kind: String,expected: String?,desired: String?,version: String,size: Long): JSONObject {
        val old=db.transaction(mapping.getString("key"),path,scope)
        if(old!=null && old.optString("kind")==kind && stamp(old)==desired && (if(old.isNull("expectedStamp"))null else old.optString("expectedStamp"))==expected && old.optString("baselineVersion")==version && old.optString("state") in setOf("created","captured","uploading","prepared","old_moved","vault_prepared","vault_old_moved","copying"))return old
        return JSONObject().put("operation",id()).put("mapping",mapping.getString("key")).put("scope",scope).put("path",path).put("kind",kind).put("expectedStamp",expected ?: JSONObject.NULL).put("stamp",desired ?: JSONObject.NULL).put("baselineVersion",version).put("size",size).put("state","created").also(db::transaction)
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
        var count=0;var size=0L;for(file in partials.listFiles().orEmpty()) {
            if(file.name.matches(Regex("[0-9a-f]{64}\\.part\\.metadata(\\.bak|\\.new)?")) && !File(partials,file.name.substringBefore(".metadata")).exists()) { file.delete();continue }
            if(file.name.matches(Regex("[0-9a-f]{64}\\.part")) && maxOf(file.lastModified(),File(file.path+".metadata").lastModified())<old) { file.delete();File(file.path+".metadata").delete();continue };if(++count>4096)throw LanLocalDeferred("private_staging_count_budget");size=Math.addExact(size,file.length());if(size>16L*1024*1024*1024)throw LanLocalDeferred("private_staging_byte_budget") };return size
    }
    private suspend fun download(client: LanProtocol,scope: String,mapping: JSONObject,path: String,remote: JSONObject,plan: String,inside: Boolean,report: LanFileReport,progress: (String,LanFileReport)->Unit,io: LanLocalFiles,localVersion: String?): File {
        val desired=remote.getString("stamp");val size=remote.getLong("size");if(size<0 || size>8L*1024*1024*1024)throw LanLocalDeferred("file_byte_budget")
        val part=privatePart(mapping,path,desired)
        val checkpoint=android.util.AtomicFile(File(part.path+".metadata"))
        val recorded=runCatching { checkpoint.openRead().bufferedReader().use { it.readText() } }.getOrNull()
        // Unknown/private partials can be re-downloaded; never trust an uncheckpointed prefix.
        if(part.exists() && (part.length()>size || recorded!=LanFileMetrics.stamp(part))) { check(part.delete());checkpoint.delete() }
        fun savePartial() {
            val out=checkpoint.startWrite()
            try { out.write(LanFileMetrics.stamp(part).toByteArray());checkpoint.finishWrite(out) }
            catch(e: Exception) { checkpoint.failWrite(out);throw e }
        }
        if(privateBytes()+size-part.length()>16L*1024*1024*1024 || partials.usableSpace<size-part.length()+256L*1024*1024)throw LanLocalDeferred("private_staging_byte_budget")
        var sourceAudio: LanAudioEnvelope?=null;var targetAudio: LanAudioEnvelope?=null;var reused=false
        if(report.canReuseAudio && inside && localVersion!=null && LanAudioEnvelope.supports(path) && part.length()<size) {
            val target=io.audioEnvelope(path,localVersion)
            if(target!=null) {
                val source=LanAudioEnvelope.from(call(client,scope,"file_audio_layout",JSONObject().put("path",path).put("expectedVersion",meta(remote)).put("mediaBytes",target.length)),size)
                if(source!=null && source.digest!=null && source.format==target.format && source.length==target.length && part.length()<=source.start) {
                    val checked=io.audioEnvelope(path,localVersion,true)
                    if(checked!=null && source.matches(checked)) { sourceAudio=source;targetAudio=checked }
                }
            }
        }
        FileOutputStream(part,true).use { output ->
            var offset=part.length();savePartial();var partialStamp=LanFileMetrics.stamp(part);var checkpointOffset=offset;var checkpointTime=android.os.SystemClock.elapsedRealtime()
            try { while(offset<size) {
                currentCoroutineContext().ensureActive()
                if(sourceAudio!=null && offset==sourceAudio.start) {
                    if(LanFileMetrics.stamp(part)!=partialStamp)throw LanLocalDeferred("private_staging_changed")
                    val copied=io.copyAudio(path,localVersion!!,targetAudio!!,output)
                    partialStamp=LanFileMetrics.stamp(part)
                    if(copied) {
                        offset=sourceAudio.end;report.reusedBytes+=sourceAudio.length;reused=true
                        output.fd.sync();savePartial();checkpointOffset=offset;checkpointTime=android.os.SystemClock.elapsedRealtime();progress(path,report)
                    } else { sourceAudio=null;targetAudio=null }
                    continue
                }
                val args=JSONObject().put("path",path).put("offset",offset)
                val count=if(sourceAudio!=null && offset<sourceAudio.start)minOf(262144L,sourceAudio.start-offset).toInt() else 262144
                if(sourceAudio!=null)args.put("count",count)
                val result=if(inside)call(client,scope,"file_read",args.put("size",size).put("sourceVersion",meta(remote))) else client.call("read",args.put("rootId",mapping.getString("rootId")).put("plan",plan))
                val bytes=Base64.decode(result.getString("data"),Base64.NO_WRAP)
                require(bytes.isNotEmpty() && bytes.size<=262144 && offset+bytes.size<=size) { "Invalid file range" }
                if(LanFileMetrics.stamp(part)!=partialStamp)throw LanLocalDeferred("private_staging_changed")
                output.write(bytes);partialStamp=LanFileMetrics.stamp(part);offset+=bytes.size
                if(offset-checkpointOffset>=8L*1024*1024 || android.os.SystemClock.elapsedRealtime()-checkpointTime>=2000) {
                    output.fd.sync();savePartial();checkpointOffset=offset;checkpointTime=android.os.SystemClock.elapsedRealtime()
                }
                report.bytes+=bytes.size;progress(path,report)
            } } finally {
                // Orderly cancellation/network errors must retain the last complete block,
                // including files interrupted before the periodic 8 MiB / 2 s checkpoint.
                if(LanFileMetrics.stamp(part)==partialStamp) { output.fd.sync();savePartial() }
            }
        }
        LanFileMetrics.setModified(part,remote.getLong("modifiedNs"));savePartial()
        if(LanFileMetrics.stamp(part)!=desired) { part.delete();throw LanLocalDeferred("download_integrity") }
        if(reused)report.reusedFiles++
        return part
    }
    private suspend fun upload(client: LanProtocol,scope: String,mapping: JSONObject,path: String,local: JSONObject,remoteHash: String?,baseline: JSONObject?,row: JSONObject,io: LanLocalFiles,resolve: Boolean,report: LanFileReport,progress: (String,LanFileReport)->Unit) {
        busy(mapping,path)
        val desired=local.getString("stamp");val part=privatePart(mapping,path,desired)
        if(!part.exists() || LanFileMetrics.stamp(part)!=desired) {
            if(privateBytes()+local.getLong("size")>16L*1024*1024*1024 || partials.usableSpace<local.getLong("size")+256L*1024*1024)throw LanLocalDeferred("private_staging_byte_budget")
            io.capture(path,part,desired)
        }
        row.put("part",part.name);store(row,"captured")
        var sourceAudio: LanAudioEnvelope?=null;var targetAudio: LanAudioEnvelope?=null;var reused=false
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
        val begin=call(client,scope,"file_begin",JSONObject().put("operation",row.getString("operation")).put("path",path).put("stamp",desired).put("size",part.length()).put("modifiedNs",local.optLong("modifiedNs")).put("editedAtMs",local.optLong("editedAtMs")).put("expectedStamp",remoteHash ?: JSONObject.NULL).put("expectedVersion",row.opt("expectedVersion") ?: JSONObject.NULL).put("baselineVersion",baseline?.optString("version").orEmpty()).put("resolve",resolve))
        if(LanUploadProgress.restartRequired(begin))store(row,"aborted")
        var offset=LanUploadProgress.start(begin,part.length())
        if(report.canReuseAudio && !begin.optBoolean("committed") && begin.optString("state")=="uploading" && offset<part.length() && !row.isNull("expectedVersion") && LanAudioEnvelope.supports(path)) {
            val source=part.inputStream().use { LanAudioEnvelope.parse(it.channel,path) }
            if(source!=null && source.length>=262144) {
                val target=LanAudioEnvelope.from(call(client,scope,"file_audio_layout",JSONObject().put("path",path).put("expectedVersion",row.getString("expectedVersion")).put("mediaBytes",source.length)),row.getString("expectedVersion").split(':')[1].toLong())
                if(target!=null && target.digest!=null && target.format==source.format && target.length==source.length) {
                    val context=currentCoroutineContext()
                    val digest=part.inputStream().use { LanAudioEnvelope.digest(it.channel,source.start,source.length) { context.ensureActive() } }
                    if(LanFileMetrics.stamp(part)!=desired)throw LanLocalDeferred("private_staging_changed")
                    val checked=source.copy(digest=digest)
                    if(checked.matches(target)) { sourceAudio=checked;targetAudio=target }
                }
            }
        }
        if(!begin.optBoolean("committed")) {
            store(row,"uploading")
            part.inputStream().use { input ->
                val buffer=ByteArray(262144)
                while(offset<part.length()) {
                    currentCoroutineContext().ensureActive()
                    if(sourceAudio!=null && offset>=sourceAudio.start && offset<sourceAudio.end) {
                        val remaining=sourceAudio.end-offset;val context=currentCoroutineContext()
                        val digest=if(offset==sourceAudio.start)sourceAudio.digest!! else part.inputStream().use { LanAudioEnvelope.digest(it.channel,offset,remaining) { context.ensureActive() } }
                        val next=call(client,scope,"file_reuse_audio",JSONObject().put("operation",row.getString("operation")).put("offset",offset).put("sourceOffset",targetAudio!!.start+offset-sourceAudio.start).put("length",remaining).put("digest",digest))
                        if(next.optBoolean("reused")) { LanUploadProgress.acknowledge(next,offset+remaining);offset+=remaining;report.reusedBytes+=remaining;reused=true;progress(path,report) }
                        else { LanUploadProgress.acknowledge(next,offset);sourceAudio=null;targetAudio=null }
                        continue
                    }
                    input.channel.position(offset)
                    val count=if(sourceAudio!=null && offset<sourceAudio.start)minOf(buffer.size.toLong(),sourceAudio.start-offset).toInt() else buffer.size
                    val n=input.read(buffer,0,count);if(n<=0)throw IOException("Local upload source unavailable")
                    val next=call(client,scope,"file_chunk",JSONObject().put("operation",row.getString("operation")).put("offset",offset).put("data",Base64.encodeToString(buffer,0,n,Base64.NO_WRAP)))
                    LanUploadProgress.acknowledge(next,offset+n);offset+=n;report.bytes+=n;progress(path,report)
                }
            }
            call(client,scope,"file_finish",JSONObject().put("operation",row.getString("operation")))
        }
        val receipt=call(client,scope,"file_upload_status",JSONObject().put("operation",row.getString("operation")))
        row.put("localVersion",row.getString("sourceVersion")).put("remoteVersion",receipt.opt("resultVersion") ?: JSONObject.NULL);store(row,"remote_committed")
        if(reused)report.reusedFiles++
    }
    private fun equivalent(mapping: JSONObject,path: String,local: JSONObject,remote: JSONObject?): Boolean {
        if(meta(local)==meta(remote))return true
        val baseline=base(db.baseline(mapping.getString("key"),path)) ?: return false
        return !baseline.migration && meta(local)==baseline.hash && meta(remote)==baseline.remoteHash
    }
    private fun ack(client: LanProtocol,scope: String,mapping: JSONObject,path: String,local: JSONObject,remote: JSONObject,baseline: JSONObject?,decision: String,row: JSONObject?): JSONObject {
        val request=JSONObject().put("path",path).put("remoteVersion",meta(remote) ?: JSONObject.NULL).put("localVersion",meta(local) ?: JSONObject.NULL).put("localPrecisionNs",local.optLong("precisionNs",1)).put("localKnown",local.optBoolean("known"))
            .put("baselineVersion",baseline?.optString("version").orEmpty()).put("operation",row?.optString("ackOperation")?.takeIf { it.isNotEmpty() } ?: id()).put("decision",decision).put("stamp",stamp(row) ?: JSONObject.NULL)
        if(row!=null) { row.put("ackOperation",request.getString("operation")).put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL);db.transaction(row) }
        if(row?.optString("kind") in setOf("upload","delete_remote"))request.put("mutationOperation",row!!.getString("operation"))
        val result=call(client,scope,"file_ack",request).put("path",path)
        db.baseline(mapping.getString("key"),result)
        if(row!=null) { db.confirmed(row);row.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { val file=File(partials,it);if(file.delete() || !file.exists())android.util.AtomicFile(File(file.path+".metadata")).delete() } }
        return result
    }
    private fun issue(mapping: JSONObject,scope: String,path: String,baseline: JSONObject?,effective: FileReconciliation.Baseline?,local: JSONObject,remote: JSONObject,reason: String,plan: String): JSONObject {
        val fingerprint=listOf(mapping.getString("key"),scope,path,baseline?.optString("version").orEmpty(),meta(local).orEmpty(),meta(remote).orEmpty()).joinToString("\n")
        return JSONObject().put("id",LanProtocol.sha(fingerprint.toByteArray())).put("mapping",mapping.getString("key")).put("scope",scope).put("rootId",mapping.getString("rootId")).put("path",path).put("baselineVersion",baseline?.optString("version").orEmpty()).put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL).put("localChange",change(effective,state(local))).put("remoteChange",change(effective,state(remote))).put("reason",reason).put("plan",plan).also(db::issue)
    }
    suspend fun synchronize(mapping: JSONObject,client: LanProtocol,onPinned: ()->Unit,progress: (String,LanFileReport)->Unit,beforeChange: (String,JSONObject?,Boolean)->Unit,afterChange: suspend ()->Unit,phase: (String,Int,Int)->Unit={ _,_,_-> }): LanFileReport {
        val report=LanFileReport();val key=mapping.getString("key")
        val started=System.nanoTime();val initialRequests=client.requestCount
        val notifyProgress: (String,LanFileReport)->Unit={ path,value ->
            if(value.bytes>0 && report.firstTransferMillis<0)report.firstTransferMillis=(System.nanoTime()-started)/1_000_000
            progress(path,value)
        }
        val startMetadata=LanFileMetrics.metadataObservations.get();val startHash=LanFileMetrics.hashBytes.get()
        val capabilities=client.call("capabilities");require(capabilities.optInt("fileProtocol")==3) { "Upgrade both devices for size+mtime file sync" }
        report.canReuseAudio=capabilities.optInt("fileAudioReuse")>=1
        val sharedConflicts=capabilities.optInt("conflictProtocol")>=1
        val newestWins=capabilities.optString("fileComparisonPolicy")=="mtime_then_size_then_last_edit"
        LanProtocol.requirePairedCapabilities(capabilities)
        val scopeInfo=call(client,"","file_open",JSONObject().put("rootId",mapping.getString("rootId")).put("mapping",key).put("selection",mapping.getJSONArray("selection")))
        val scope=scopeInfo.getString("scope");val revision=scopeInfo.getString("revision")
        if(db.hasPendingTransactions(key,scope))throw LanLocalDeferred("mapping_scope_changed")
        val selection=mapping.getJSONArray("selection").let { a -> List(a.length()) { a.getString(it) } }
        val deltaEnabled=capabilities.optInt("fileDeltaProtocol")>=1
        var offset=0
        if(!deltaEnabled) {
            db.resetSnapshots(key)
            phase("reading_baseline",0,scopeInfo.getInt("total"))
            do {
                currentCoroutineContext().ensureActive();val page=call(client,scope,"file_rows",JSONObject().put("offset",offset).put("revision",revision));val rows=page.getJSONArray("rows")
                db.baselines(key,List(rows.length()) { rows.getJSONObject(it) });offset=page.getInt("next");phase("reading_baseline",offset,scopeInfo.getInt("total"))
            } while(page.optBoolean("hasMore"))
        }
        phase("pc_scan",0,0)
        val plan=client.call("plan",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("selection",mapping.getJSONArray("selection")))
        val planId=plan.getString("plan");offset=0
        report.manifestCacheHit=plan.optBoolean("cacheHit")
        var delta: JSONObject?=null
        if(deltaEnabled) {
            phase("reading_delta",0,0)
            delta=call(client,scope,"file_delta",JSONObject().put("plan",planId).put("cursor",db.metadataCursor(key,scope)))
            report.incremental=!delta.getBoolean("reset")
            db.beginDelta(key,delta.getBoolean("reset"))
        }
        localFactory(mapping,scopeInfo.getLong("total")==0L).use { io ->
            onPinned()
            phase("local_recovery",0,0)
            var journalOffset=0
            while(true) {
                val rows=db.transactions(key,journalOffset);if(rows.isEmpty())break
                for(row in rows.filter { it.optString("scope")==scope && it.optString("kind") in setOf("download","delete_local") }) {
                    if(row.optString("state") !in setOf("acknowledged","aborted"))io.indexChanged(fileIndex,row.getString("path"))
                    val part=row.optString("part").takeIf { it.matches(Regex("[0-9a-f]{64}\\.part")) }?.let { File(partials,it) }
                    io.recover(row,part)?.let { report.defer(row.getString("path")+": "+it);db.hold(key,row.getString("path")) }
                }
                journalOffset+=rows.size
            }
            suspend fun reconcile(paths: List<String>,cues: Boolean,allowDeletes: Boolean): List<String> {
                if(paths.isNotEmpty() && report.preparationMillis<0)report.preparationMillis=(System.nanoTime()-started)/1_000_000
                val postponed=mutableListOf<String>()
                for(path in paths) {
                        currentCoroutineContext().ensureActive();notifyProgress(path,report)
                        try {
                            if(db.held(key,path))throw LanLocalDeferred("local_recovery_required")
                            val inside=selected(path,selection)

                            var local=db.entry(key,"local",path)?.takeIf { it.optBoolean("known") } ?: io.observe(path)
                            var remote=db.entry(key,"remote",path)?.put("known",true) ?: if(inside)LanFileMetrics.absent() else JSONObject().put("known",false)
                            var baseline=db.baseline(key,path)
                            var pending=db.transaction(key,path,scope)
                            if(pending?.optString("kind") in setOf("upload","delete_remote") && pending!!.optString("state") in setOf("captured","uploading","remote_committed")) {
                                val status=call(client,scope,"file_upload_status",JSONObject().put("operation",pending.getString("operation")))
                                if(LanUploadProgress.restartRequired(status)) { store(pending,"aborted");pending=null }
                                else if(status.optBoolean("committed")) { if(pending.optString("kind")=="upload" && pending.has("sourceVersion"))pending.put("localVersion",pending.getString("sourceVersion")).put("remoteVersion",status.opt("resultVersion") ?: JSONObject.NULL);if(pending.optString("kind")=="delete_remote")pending.put("localVersion",JSONObject.NULL).put("remoteVersion",JSONObject.NULL);store(pending,"remote_committed") }
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
                                    val desired=stamp(pending)
                                    val verifiedRemote=call(client,scope,"file_verify",JSONObject().put("path",path).put("expectedVersion",meta(remote) ?: JSONObject.NULL))
                                    val verifiedLocal=io.verify(path,meta(local))
                                    val source=pending.optString("sourceVersion")
                                    val sourceUnchanged=when(pending.optString("kind")) { "upload"->meta(local)==source;"download"->meta(remote)==source;else->true }
                                    if(stamp(verifiedRemote)==desired && stamp(verifiedLocal)==desired && sourceUnchanged)pending.put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL).also(db::transaction)
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
                            // The last-edit store is consulted only for the size tie-breaker.
                            // Ordinary scans and equal metadata never read it.
                            if(newestWins && inside && meta(local)!=null && meta(remote)!=null && local.optLong("modifiedNs")==remote.optLong("modifiedNs") && local.optLong("size")!=remote.optLong("size")) {
                                local=io.observe(path,includeEditTime=true)
                                remote=call(client,scope,"file_probe",JSONObject().put("path",path))
                            }
                            var decision=if(inside)FileReconciliation.decide(effective,state(local),state(remote),newestWins) else when {
                                equivalent(mapping,path,local,remote)->FileReconciliation.Decision("none")
                                meta(local)==null->FileReconciliation.Decision("download")
                                else->FileReconciliation.Decision("conflict","initial_different_content")
                            }
                            var resolve=decision.reason in setOf("newer_file_mtime","newer_edit_time")
                            if(decision.action=="conflict") {
                                val conflict=issue(mapping,scope,path,baseline,effective,local,remote,decision.reason!!,planId)
                                if(sharedConflicts) {
                                    val shared=call(client,scope,"file_conflict",JSONObject(conflict.toString()))
                                    val queued=db.issue(conflict.getString("id"))?.optString("choice")
                                    if(queued in setOf("local","remote") && shared.optString("choice") !in setOf("local","remote")) {
                                        val chosen=call(client,scope,"file_conflict_choose",JSONObject().put("id",conflict.getString("id")).put("choice",queued))
                                        shared.put("choice",chosen.getString("choice"))
                                    }
                                    for(field in listOf("local","remote","choice","localChange","remoteChange","reportedAt"))conflict.put(field,shared.opt(field) ?: JSONObject.NULL)
                                    conflict.put("shared",true);db.updateIssue(conflict)
                                }
                                val choice=if(sharedConflicts)conflict.optString("choice") else db.issue(conflict.getString("id"))?.optString("choice")
                                if(choice in setOf("local","remote") && inside) { resolve=true;decision=if(choice=="local")FileReconciliation.Decision(if(meta(local)==null) "delete_remote" else "upload") else FileReconciliation.Decision(if(meta(remote)==null) "delete_local" else "download") }
                                else { report.conflict(conflict);if(!cues)db.hold(key,path);report.processed++;continue }
                            }
                            if(decision.action=="defer")throw LanLocalDeferred(decision.reason!!)
                            if(!allowDeletes && decision.action in setOf("delete_local","delete_remote")) { postponed+=path;continue }
                            val version=baseline?.optString("version").orEmpty()
                            var transaction: JSONObject?=null
                            if(decision.action in setOf("download","upload","delete_local","delete_remote")) {
                                phase("transferring",report.processed,0)
                                busy(mapping,path)
                                local=io.verify(path,meta(local))
                                remote=if(inside)call(client,scope,"file_verify",JSONObject().put("path",path).put("expectedVersion",meta(remote) ?: JSONObject.NULL)) else client.call("verify",JSONObject().put("fileProtocol",3).put("rootId",mapping.getString("rootId")).put("plan",planId).put("path",path).put("expectedVersion",meta(remote)))
                            }
                            when(decision.action) {
                                "download" -> {
                                    val part=download(client,scope,mapping,path,remote,planId,inside,report,notifyProgress,io,meta(local))
                                    if(cues) {
                                        val dependencies=cueReferences(part,path)
                                        for(reference in dependencies) {
                                            val here=io.observe(reference);val there=if(selected(reference,selection))call(client,scope,"file_probe",JSONObject().put("path",reference)) else db.entry(key,"remote",reference)
                                            if(db.held(key,reference) || meta(here)==null || !equivalent(mapping,reference,here,there))throw LanLocalDeferred("cue_dependency_not_synchronized")
                                        }
                                        remote.put("dependencies",JSONArray(dependencies))
                                    }
                                    transaction=transaction(mapping,scope,path,"download",stamp(local),stamp(remote),version,remote.getLong("size")).put("part",part.name).put("modifiedNs",remote.getLong("modifiedNs")).put("editedAtMs",remote.optLong("editedAtMs")).put("sourceVersion",meta(remote)).put("expectedVersion",meta(local) ?: JSONObject.NULL)
                                    store(transaction,"created");io.indexChanged(fileIndex,path);beforeChange(path,remote,false)
                                    io.install(path,part,transaction) {
                                        busy(mapping,path)
                                        if(inside) { val current=call(client,scope,"file_probe",JSONObject().put("path",path));if(meta(current)!=meta(remote))throw LanLocalDeferred("remote_changed_before_commit") } else client.call("read",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("path",path).put("offset",0))
                                    }
                                    local=io.observe(path)
                                }
                                "upload" -> {
                                    transaction=transaction(mapping,scope,path,"upload",stamp(remote),stamp(local),version,local.getLong("size")).put("sourceVersion",meta(local)).put("expectedVersion",meta(remote) ?: JSONObject.NULL)
                                    store(transaction,"created")
                                    upload(client,scope,mapping,path,local,stamp(remote),baseline,transaction,io,resolve,report,notifyProgress)
                                    remote=call(client,scope,"file_probe",JSONObject().put("path",path))
                                    if(meta(io.observe(path))!=meta(local))throw LanLocalDeferred("local_changed_during_upload")
                                }
                                "delete_local" -> {
                                    transaction=transaction(mapping,scope,path,"delete_local",stamp(local),null,version,0).put("expectedVersion",meta(local) ?: JSONObject.NULL);io.indexChanged(fileIndex,path);beforeChange(path,null,true)
                                    io.delete(path,transaction) { busy(mapping,path);if(meta(call(client,scope,"file_probe",JSONObject().put("path",path)))!=null)throw LanLocalDeferred("remote_changed_before_commit") }
                                    local=LanFileMetrics.absent()
                                }
                                "delete_remote" -> {
                                    if(meta(io.observe(path))!=null)throw LanLocalDeferred("local_changed_before_delete")
                                    transaction=transaction(mapping,scope,path,"delete_remote",stamp(remote),null,version,0)
                                    call(client,scope,"file_delete",JSONObject().put("path",path).put("operation",transaction.getString("operation")).put("expectedStamp",stamp(remote)).put("expectedVersion",meta(remote)).put("baselineVersion",version).put("resolve",resolve));transaction.put("localVersion",JSONObject.NULL).put("remoteVersion",JSONObject.NULL);store(transaction,"remote_committed")
                                    remote=LanFileMetrics.absent()
                                }
                            }
                            if(decision.action in setOf("download","delete_local"))io.indexChanged(fileIndex,path)
                            if(decision.action!="none") {
                                // Recheck both metadata snapshots after all transfer I/O, before persisting the mapping.
                                if(meta(io.observe(path))!=meta(local))throw LanLocalDeferred("local_changed_before_ack")
                                if(inside)ack(client,scope,mapping,path,local,remote,baseline,decision.action,transaction ?: pending) else { db.dependencyBaseline(key,path,meta(local)!!,meta(remote)!!);if(transaction!=null)db.confirmed(transaction) }
                                report.actions[decision.action]=(report.actions[decision.action] ?: 0)+1
                            }
                            if(cues && decision.action!="none" && meta(local)!=null)beforeChange(path,remote,false)
                            if(sharedConflicts && db.issueForPath(key,path)!=null)call(client,scope,"file_conflict_clear",JSONObject().put("path",path).put("localVersion",meta(local) ?: JSONObject.NULL).put("remoteVersion",meta(remote) ?: JSONObject.NULL))
                            db.resolved(key,path)
                            if(decision.action!="none")afterChange()
                        } catch(e: kotlinx.coroutines.CancellationException) { throw e }
                        catch(e: LanRemoteFileConflict) { report.defer("$path: ${e.reason}; refresh the snapshot");if(!cues)db.hold(key,path) }
                        catch(e: LanLocalDeferred) { android.util.Log.w("LanFileSync","$path: ${e.reason}");report.defer("$path: ${e.reason}");if(!cues)db.hold(key,path) }
                        catch(e: SyncWifiRequiredException) { throw e }
                        catch(e: IOException) { android.util.Log.w("LanFileSync","$path: peer_or_local_file_unavailable",e);report.defer("$path: peer_or_local_file_unavailable");if(!cues)db.hold(key,path) }
                        report.processed++
                }
                return postponed
            }
            // Start changed PC files as each metadata page arrives. Unchanged PC entries
            // wait for the streamed phone scan, avoiding a second stat of every unchanged file.
            phase(if(deltaEnabled)"reading_delta" else "reading_manifest",0,delta?.getInt("total") ?: plan.getInt("total"))
            if(deltaEnabled) {
                var page=delta!!
                while(true) {
                    currentCoroutineContext().ensureActive()
                    val rows=page.getJSONArray("rows").let { a -> List(a.length()) { a.getJSONObject(it) } }
                    db.deltaRows(key,rows);report.metadataRows+=rows.size
                    // Every changed baseline/remote path is revisited, but local state is
                    // observed afresh. Retained local observations are never authoritative.
                    val candidates=rows.map { it.getString("path") }.filterNot { it.endsWith(".cue",true) }
                    db.queueDelta(key,candidates)
                    if(candidates.isNotEmpty()) {
                        db.markScheduled(key,candidates)
                        phase("reconciling",0,0)
                        db.postponeDeletes(key,reconcile(candidates,false,false))
                    }
                    phase("reading_delta",page.getInt("next"),page.getInt("total"))
                    if(!page.optBoolean("hasMore"))break
                    page=call(client,scope,"file_delta_rows",JSONObject().put("cursor",delta.getString("cursor")).put("offset",page.getInt("next")))
                }
            } else while(offset<plan.getInt("total")) {
                currentCoroutineContext().ensureActive()
                val page=client.call("manifest",JSONObject().put("rootId",mapping.getString("rootId")).put("plan",planId).put("offset",offset)).getJSONArray("files")
                require(page.length()>0)
                val rows=List(page.length()) { page.getJSONObject(it) }
                val candidates=rows.filter { row ->
                    val path=row.getString("path");val previous=base(db.baseline(key,path))
                    !path.endsWith(".cue",true) && (previous==null || previous.migration || previous.remoteHash!=meta(row) || db.transaction(key,path,scope)!=null || db.issueForPath(key,path)!=null)
                }.map { it.getString("path") }
                db.entries(key,"remote",rows,candidates);offset+=rows.size
                report.metadataRows+=rows.size
                phase("reading_manifest",offset,plan.getInt("total"))
                if(candidates.isNotEmpty()) {
                    phase("reconciling",0,0)
                    db.postponeDeletes(key,reconcile(candidates,false,false))
                }
            }
            client.close()
            val scanned=ArrayList<JSONObject>(200);var scannedCount=0
            phase("phone_scan",0,0)
            suspend fun flushScan() {
                if(scanned.isEmpty())return
                val rows=scanned.toList()
                val changes=if(!deltaEnabled)rows else rows.filter { row ->
                    val path=row.getString("path");val previous=base(db.baseline(key,path))
                    val pending=db.transaction(key,path,scope)
                    !row.optBoolean("known",true) || previous==null || previous.migration || previous.hash!=meta(row) ||
                        db.entry(key,"last_local",path)?.optLong("editedAtMs")!=row.optLong("editedAtMs") ||
                        pending!=null && pending.optString("state") !in setOf("acknowledged","aborted") || db.issueForPath(key,path)!=null
                }
                if(deltaEnabled)db.queueDelta(key,changes.map { it.getString("path") })
                val candidates=changes.map { it.getString("path") }.filter { !it.endsWith(".cue",true) && !db.scheduled(key,it) }
                db.entries(key,"local",rows,candidates);scannedCount+=rows.size;scanned.clear()
                if(candidates.isNotEmpty()) {
                    phase("reconciling",0,0)
                    db.postponeDeletes(key,reconcile(candidates,false,false))
                }
                phase("phone_scan",scannedCount,0)
            }
            val emit: suspend (JSONObject)->Unit={ scanned.add(it);if(scanned.size==200)flushScan() }
            val unavailable: (String)->Unit={ report.defer("$it: local_directory_unknown") }
            if(fileIndex!=null) {
                val indexResult=io.indexedScan(fileIndex,selection,emit,unavailable)
                report.localIndexHit=indexResult.hit;report.localIndexRefreshed=indexResult.refreshed;report.libraryMetadataReused=indexResult.shared
            } else io.scan(selection,emit,unavailable)
            flushScan();db.requirePortableLayout(key)
            if(deltaEnabled) {
                // CUE may refer to audio outside the selected subtree. Those files are not
                // visited by the regular scan; retain the existing read-only dependency repair.
                var dependencyOffset=0
                while(true) {
                    val rows=db.dependencyEntries(key,dependencyOffset);if(rows.isEmpty())break
                    for(remote in rows) {
                        val path=remote.getString("path");if(selected(path,selection))continue
                        val local=try { io.observe(path) }catch(_: IOException) { JSONObject().put("known",false) }
                        db.entry(key,"local",local.put("path",path))
                        if(!local.optBoolean("known",true) || !equivalent(mapping,path,local,remote))db.queueDelta(key,listOf(path))
                    }
                    dependencyOffset+=rows.size
                }
                db.completeDeltaScan(key)
            }
            // Only complete snapshots can establish deletions. CUE remains last so its
            // audio dependencies have finished; per-file commit preconditions stay intact.
            phase("reconciling",0,0)
            for(cues in listOf(false,true)) {
                offset=0
                while(true) {
                    val paths=if(deltaEnabled)db.deltaPaths(key,offset,cues) else db.paths(key,offset,cues);if(paths.isEmpty())break
                    reconcile(paths.filterNot { db.scheduled(key,it) },cues,true)
                    offset+=paths.size
                }
            }
        }
        if(deltaEnabled && report.deferredCount==0 && report.conflictCount==0) {
            val cursor=delta!!.getString("cursor")
            call(client,scope,"file_delta_commit",JSONObject().put("cursor",cursor))
            db.confirmDelta(key,scope,cursor)
        }
        report.metadataObservations=LanFileMetrics.metadataObservations.get()-startMetadata;report.hashBytes=LanFileMetrics.hashBytes.get()-startHash
        if(report.preparationMillis<0)report.preparationMillis=(System.nanoTime()-started)/1_000_000
        report.protocolRequests=client.requestCount-initialRequests
        return report
    }
    fun issues(mapping: String)=db.issues(mapping)
    fun choose(id: String,choice: String)=db.choose(id,choice)
}
