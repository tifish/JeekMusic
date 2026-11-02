package com.tinyfish.jeekmusic.data.sync

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.os.storage.StorageManager
import android.provider.DocumentsContract
import com.tinyfish.jeekmusic.data.scanner.SafPaths
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONArray
import org.json.JSONObject
import java.io.Closeable
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest
import java.util.UUID

internal class LanLocalDeferred(val reason: String): IOException(reason)

/** Real descriptor-relative transactions. Factory enforces the selected SAF tree plus OS access.
 * Root identity is a local marker, never a remote rootId or a filename namespace. */
internal class LanLocalFiles(private val absoluteRoot: String,private val mapping: JSONObject,private val database: LanFileDatabase,allowPin: Boolean): Closeable {
    private val root=ParcelFileDescriptor.adoptFd(LanAtomicFiles.openRoot(absoluteRoot))
    private val rootIdentity=LanAtomicFiles.identity(root.fd)
    private val markerName=".jeek-sync-directory-id"
    private val maxBytes=8L*1024*1024*1024
    private var atomicRenameSupported: Boolean?=null
    private val supported=setOf("mp3","flac","m4a","mp4","aac","ogg","oga","opus","wav","mka","amr","cue","lrc","jpg","jpeg","png","webp")
    init {
        try {
            val expected=mapping.optString("directoryIdentity")
            var fd=LanAtomicFiles.openLeaf(root.fd,markerName,false,false)
            if(fd<0) {
                if(expected.isNotEmpty() || !allowPin)throw LanLocalDeferred("directory_identity_missing")
                fd=LanAtomicFiles.openLeaf(root.fd,markerName,true,true)
                ParcelFileDescriptor.adoptFd(fd).use { p ->
                    val identity=UUID.randomUUID().toString()
                    write(p,"JeekMusic-sync-directory-v1\n$identity\n".toByteArray(Charsets.UTF_8));p.fileDescriptor.sync()
                }
                fd=LanAtomicFiles.openLeaf(root.fd,markerName,false,false)
            }
            val actual=ParcelFileDescriptor.adoptFd(fd).use { p ->
                if(LanAtomicFiles.identity(p.fd)[2]>128)throw LanLocalDeferred("directory_identity_invalid")
                val buffer=ByteArray(129);var count=0;while(count<buffer.size) { val n=read(p,buffer,count,buffer.size-count);if(n<=0)break;count+=n }
                if(count>128)throw LanLocalDeferred("directory_identity_invalid")
                val text=Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(buffer,0,count)).toString()
                val lines=text.lines();if(lines.size!=3 || lines[0]!="JeekMusic-sync-directory-v1" || runCatching { UUID.fromString(lines[1]).toString()==lines[1] }.getOrDefault(false)!=true)throw LanLocalDeferred("directory_identity_invalid")
                lines[1]
            }
            if(expected.isNotEmpty() && expected!=actual || expected.isEmpty() && !allowPin)throw LanLocalDeferred("directory_identity_changed")
            mapping.put("directoryIdentity",actual)
        } catch(e: Exception) { root.close();throw e }
    }
    companion object {
        fun fromMapping(context: Context,mapping: JSONObject,database: LanFileDatabase,allowPin: Boolean): LanLocalFiles {
            LanMappingTargets.requireCurrent(mapping)
            if(!Environment.isExternalStorageManager())throw LanLocalDeferred("all_files_access_required")
            val uri=Uri.parse(mapping.getString("treeUri"));require(uri.authority=="com.android.externalstorage.documents")
            val id=DocumentsContract.getTreeDocumentId(uri);require(id==mapping.getString("documentId"))
            val grant=context.contentResolver.persistedUriPermissions.any { it.isReadPermission && it.isWritePermission && it.uri.authority==uri.authority && runCatching { DocumentsContract.getTreeDocumentId(it.uri)==id }.getOrDefault(false) }
            if(!grant)throw LanLocalDeferred("saf_permission_missing")
            val path=SafPaths.fromTreeDocumentId(id) ?: throw LanLocalDeferred("storage_unavailable")
            require(path.relativePath==mapping.getString("relative") && path.volumeName==mapping.getString("volume"))
            val volume=context.getSystemService(StorageManager::class.java).storageVolumes.singleOrNull { it.mediaStoreVolumeName==path.volumeName }?.directory ?: throw LanLocalDeferred("storage_unavailable")
            val target=File(volume,path.relativePath.trimEnd('/'))
            require(target.canonicalPath==target.absolutePath) { "Linked destination root rejected" }
            return LanLocalFiles(target.path,mapping,database,allowPin)
        }
    }
    private fun currentRoot() {
        ParcelFileDescriptor.adoptFd(LanAtomicFiles.openRoot(absoluteRoot)).use { now ->
            val current=LanAtomicFiles.identity(now.fd);if(current[0]!=rootIdentity[0] || current[1]!=rootIdentity[1])throw LanLocalDeferred("directory_identity_changed")
        }
    }
    private fun parent(path: String,create: Boolean=false): ParcelFileDescriptor? {
        LanProtocol.relative(path);currentRoot()
        val fd=LanAtomicFiles.openParent(root.fd,path.substringBeforeLast('/',""),create)
        return if(fd<0)null else ParcelFileDescriptor.adoptFd(fd)
    }
    private fun read(file: ParcelFileDescriptor,buffer: ByteArray,offset: Int=0,length: Int=buffer.size): Int = try { android.system.Os.read(file.fileDescriptor,buffer,offset,length) }catch(e: android.system.ErrnoException) { throw IOException("file_read_unavailable",e) }
    private fun write(file: ParcelFileDescriptor,buffer: ByteArray,length: Int=buffer.size) {
        var offset=0;while(offset<length) { val n=try { android.system.Os.write(file.fileDescriptor,buffer,offset,length-offset) }catch(e: android.system.ErrnoException) { throw IOException("file_write_unavailable",e) };if(n<=0)throw IOException("file_write_unavailable");offset+=n }
    }
    private fun same(a: LongArray,b: LongArray)=a[0]==b[0] && a[1]==b[1]
    private fun identity(row: JSONObject,key: String)=row.optJSONArray(key)?.let { a -> LongArray(a.length()) { a.getLong(it) } }
    private suspend fun hash(file: ParcelFileDescriptor): String {
        val before=LanAtomicFiles.identity(file.fd);if(before[2]>maxBytes)throw LanLocalDeferred("file_byte_budget")
        android.system.Os.lseek(file.fileDescriptor,0,android.system.OsConstants.SEEK_SET)
        val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(128*1024);var bytes=0L
        while(true) { currentCoroutineContext().ensureActive();val n=read(file,buffer);if(n<=0)break;bytes+=n;if(bytes>maxBytes)throw LanLocalDeferred("file_byte_budget");digest.update(buffer,0,n) }
        val after=LanAtomicFiles.identity(file.fd)
        if(!before.contentEquals(after) || bytes!=before[2])throw LanLocalDeferred("local_changed_during_read")
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
    suspend fun observe(path: String): JSONObject {
        val parent=parent(path) ?: return JSONObject().put("known",true).put("hash",JSONObject.NULL)
        return parent.use { p ->
            val fd=LanAtomicFiles.openLeaf(p.fd,path.substringAfterLast('/'),false,false)
            if(fd<0) JSONObject().put("known",true).put("hash",JSONObject.NULL)
            else ParcelFileDescriptor.adoptFd(fd).use { file ->
                val value=hash(file);val id=LanAtomicFiles.identity(file.fd)
                JSONObject().put("known",true).put("hash",value).put("size",id[2]).put("modifiedNs",Math.addExact(Math.multiplyExact(id[3],1_000_000_000),id[4]))
            }
        }
    }
    suspend fun scan(selection: List<String>,emit: (JSONObject)->Unit,unavailable: (String)->Unit) {
        var visited=0;var files=0;var bytes=0L
        suspend fun walk(path: String,depth: Int) {
            currentCoroutineContext().ensureActive();if(depth>64 || ++visited>250000)throw LanLocalDeferred("scan_work_budget")
            val raw=LanAtomicFiles.openParent(root.fd,path,false);if(raw<0)return
            ParcelFileDescriptor.adoptFd(raw).use { directory ->
                var cookie=0L
                while(true) {
                    currentCoroutineContext().ensureActive();currentRoot();val page=LanAtomicFiles.listPage(directory.fd,cookie);cookie=page[0].toString(Charsets.US_ASCII).toLong()
                    for(item in page.drop(1)) {
                        if(++visited>250000)throw LanLocalDeferred("scan_work_budget")
                        val name=Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(item,1,item.size-1)).toString()
                        if(name.startsWith('.'))continue
                        val relative=if(path.isEmpty())name else "$path/$name"
                        LanProtocol.relative(relative)
                        if(item[0].toInt().toChar()=='D') { try { walk(relative,depth+1) }catch(e: LanLocalDeferred) { throw e }catch(_: IOException) { unavailable(relative) } }
                        else if(item[0].toInt().toChar()=='F' && name.substringAfterLast('.').lowercase(java.util.Locale.ROOT) in supported) {
                            if(++files>100000)throw LanLocalDeferred("scan_file_budget")
                            val row=try { observe(relative) }catch(_: IOException) { JSONObject().put("known",false) }
                            bytes=Math.addExact(bytes,row.optLong("size"));if(bytes>500L*1024*1024*1024)throw LanLocalDeferred("scan_byte_budget")
                            emit(row.put("path",relative))
                        }
                    }
                    if(page.size==1)break
                }
            }
        }
        for(path in selection.distinct()) { LanProtocol.relative(path,true);try { walk(path,0) }catch(e: LanLocalDeferred) { throw e }catch(_: IOException) { unavailable(path) } }
    }
    suspend fun capture(path: String,target: File,expected: String): Long {
        val p=parent(path) ?: throw LanLocalDeferred("local_changed_before_upload")
        return p.use { parent ->
            val fd=LanAtomicFiles.openLeaf(parent.fd,path.substringAfterLast('/'),false,false);if(fd<0)throw LanLocalDeferred("local_changed_before_upload")
            ParcelFileDescriptor.adoptFd(fd).use { file ->
                val before=LanAtomicFiles.identity(file.fd);if(before[2]>maxBytes)throw LanLocalDeferred("file_byte_budget")
                val digest=MessageDigest.getInstance("SHA-256")
                FileOutputStream(target).use { output -> val buffer=ByteArray(128*1024);var bytes=0L;while(true) { currentCoroutineContext().ensureActive();val n=read(file,buffer);if(n<=0)break;bytes+=n;if(bytes>maxBytes)throw LanLocalDeferred("file_byte_budget");digest.update(buffer,0,n);output.write(buffer,0,n) };output.fd.sync() }
                if(!before.contentEquals(LanAtomicFiles.identity(file.fd)) || digest.digest().joinToString("") { "%02x".format(it) }!=expected)throw LanLocalDeferred("local_changed_before_upload")
                before[2]
            }
        }
    }
    private fun save(row: JSONObject,state: String) { row.put("state",state).put("updatedAt",System.currentTimeMillis());database.transaction(row) }
    private suspend fun prepareStage(parent: Int,part: File,row: JSONObject): ParcelFileDescriptor {
        if(database.stagingBytes(row.getString("operation"))+part.length()>16L*1024*1024*1024 || File(absoluteRoot).usableSpace<part.length()+64L*1024*1024)throw LanLocalDeferred("staging_byte_budget")
        val name=".jeek-sync-${row.getString("operation")}.new"
        var fd=LanAtomicFiles.openLeaf(parent,name,true,false);val fresh=fd<0
        if(fresh)fd=LanAtomicFiles.openLeaf(parent,name,true,true)
        val file=ParcelFileDescriptor.adoptFd(fd)
        try {
            val id=LanAtomicFiles.identity(file.fd);val saved=identity(row,"stageIdentity")
            if(!fresh && (saved==null && id[2]!=0L || saved!=null && !same(saved,id)))throw LanLocalDeferred("stage_identity_changed")
            row.put("stageIdentity",JSONArray(id.toList()));save(row,"prepared")
            android.system.Os.ftruncate(file.fileDescriptor,0)
            part.inputStream().use { input -> val buffer=ByteArray(128*1024);var bytes=0L;while(true) { currentCoroutineContext().ensureActive();val n=input.read(buffer);if(n<0)break;bytes+=n;if(bytes>maxBytes)throw LanLocalDeferred("file_byte_budget");write(file,buffer,n) } }
            file.fileDescriptor.sync()
            if(hash(file)!=row.getString("hash"))throw LanLocalDeferred("download_integrity")
            if(row.optLong("modifiedNs")>0) { LanAtomicFiles.setModified(file.fd,row.getLong("modifiedNs"));file.fileDescriptor.sync() }
            return file
        } catch(e: Exception) { file.close();throw e }
    }
    private suspend fun installAtomic(path: String,part: File,row: JSONObject,beforeCommit: suspend ()->Unit) {
        val p=parent(path,true) ?: throw LanLocalDeferred("storage_unavailable")
        p.use { parent ->
            val name=path.substringAfterLast('/');val op=row.getString("operation");val stageName=".jeek-sync-$op.new";val backupName=".jeek-sync-$op.bak"
            val stage=prepareStage(parent.fd,part,row)
            var original: ParcelFileDescriptor?=null;var moved=false;var installed=false
            try {
                val fd=LanAtomicFiles.openLeaf(parent.fd,name,true,false);if(fd>=0)original=ParcelFileDescriptor.adoptFd(fd)
                val expected=if(row.isNull("expectedHash"))null else row.getString("expectedHash")
                val actual=original?.let { hash(it) }
                if(actual==row.getString("hash")) { save(row,"committed");return }
                if(actual!=expected)throw LanLocalDeferred("local_changed_before_commit")
                if(original!=null && database.backupBytes()+LanAtomicFiles.identity(original.fd)[2]>32L*1024*1024*1024)throw LanLocalDeferred("backup_byte_budget")
                beforeCommit();currentCoroutineContext().ensureActive();currentRoot()
                if(original!=null) {
                    if(hash(original)!=expected)throw LanLocalDeferred("local_changed_before_commit")
                    val id=LanAtomicFiles.identity(original.fd);row.put("backupIdentity",JSONArray(id.toList())).put("backupBytes",id[2]);save(row,"prepared")
                    LanAtomicFiles.moveNoReplace(parent.fd,name,backupName,id[0],id[1]);moved=true;save(row,"old_moved")
                    if(hash(original)!=expected)throw LanLocalDeferred("local_changed_during_commit")
                }
                val id=LanAtomicFiles.identity(stage.fd);LanAtomicFiles.moveNoReplace(parent.fd,stageName,name,id[0],id[1]);installed=true
                if(hash(stage)!=row.getString("hash"))throw LanLocalDeferred("local_changed_during_commit")
                save(row,"committed")
            } finally {
                if(!installed && moved && original!=null)runCatching { val id=LanAtomicFiles.identity(original.fd);LanAtomicFiles.moveNoReplace(parent.fd,backupName,name,id[0],id[1]);row.remove("backupIdentity");row.remove("backupBytes");save(row,"interrupted") }
                if(!installed)runCatching { val id=LanAtomicFiles.identity(stage.fd);LanAtomicFiles.removeOwned(parent.fd,stageName,id[0],id[1]) }
                if(!installed && !moved)runCatching { if(LanAtomicFiles.statLeaf(parent.fd,backupName)==null) { row.remove("backupIdentity");row.remove("backupBytes");save(row,"interrupted") } }
                original?.close();stage.close()
            }
        }
    }
    private suspend fun deleteAtomic(path: String,row: JSONObject,beforeCommit: suspend ()->Unit) {
        val p=parent(path) ?: throw LanLocalDeferred("local_changed_before_commit")
        p.use { parent ->
            val name=path.substringAfterLast('/');val fd=LanAtomicFiles.openLeaf(parent.fd,name,true,false);if(fd<0) { save(row,"committed");return }
            ParcelFileDescriptor.adoptFd(fd).use { file ->
                if(hash(file)!=row.getString("expectedHash"))throw LanLocalDeferred("local_changed_before_commit")
                val id=LanAtomicFiles.identity(file.fd);if(database.backupBytes()+id[2]>32L*1024*1024*1024)throw LanLocalDeferred("backup_byte_budget")
                row.put("backupIdentity",JSONArray(id.toList())).put("backupBytes",id[2]);save(row,"prepared");beforeCommit();currentCoroutineContext().ensureActive();currentRoot()
                if(hash(file)!=row.getString("expectedHash"))throw LanLocalDeferred("local_changed_before_commit")
                val backupName=".jeek-sync-${row.getString("operation")}.bak"
                LanAtomicFiles.moveNoReplace(parent.fd,name,backupName,id[0],id[1])
                if(hash(file)!=row.getString("expectedHash")) {
                    runCatching { LanAtomicFiles.moveNoReplace(parent.fd,backupName,name,id[0],id[1]);row.remove("backupIdentity");row.remove("backupBytes");save(row,"interrupted") }
                    throw LanLocalDeferred("local_changed_during_commit")
                }
                save(row,"committed")
            }
        }
    }
    /** Public-storage FUSE can reject RENAME_NOREPLACE. Public names then use exclusive
     * creation + a durable owned-inode copy, while originals move into a journal-pinned vault.
     * The app scanner excludes the unconfirmed public name; other apps can see a partial copy. */
    suspend fun install(path: String,part: File,row: JSONObject,beforeCommit: suspend ()->Unit) {
        if(atomicRenameSupported==false || row.optString("commitMode")=="conditional_copy") { row.put("commitMode","conditional_copy");return installConditional(path,part,row,beforeCommit) }
        try { installAtomic(path,part,row,beforeCommit) }
        catch(e: IOException) {
            if(e.message!="rename_noreplace_unsupported")throw e
            atomicRenameSupported=false
            row.remove("backupIdentity");row.remove("backupBytes");row.put("commitMode","conditional_copy");save(row,"created")
            installConditional(path,part,row,beforeCommit)
        }
    }
    suspend fun delete(path: String,row: JSONObject,beforeCommit: suspend ()->Unit) {
        try { deleteAtomic(path,row,beforeCommit) }
        catch(e: IOException) {
            if(e.message!="rename_noreplace_unsupported")throw e
            atomicRenameSupported=false
            row.remove("backupIdentity");row.remove("backupBytes");row.put("commitMode","conditional_copy");save(row,"created")
            val p=parent(path) ?: throw LanLocalDeferred("storage_unavailable")
            p.use { directory ->
                beforeCommit();currentRoot();moveOriginal(directory.fd,path.substringAfterLast('/'),row)
                if(LanAtomicFiles.statLeaf(directory.fd,path.substringAfterLast('/'))!=null)throw LanLocalDeferred("local_changed_during_commit")
                save(row,"committed")
            }
        }
    }
    private fun vault(parent: Int,row: JSONObject,create: Boolean): ParcelFileDescriptor {
        val name=".jeek-sync-${row.getString("operation")}.recovery"
        require(row.getString("operation").matches(Regex("[0-9a-f]{32}")))
        val existing=identity(row,"vaultIdentity")
        val value=ParcelFileDescriptor.adoptFd(LanAtomicFiles.openVault(parent,name,create && existing==null))
        try {
            val actual=LanAtomicFiles.identity(value.fd)
            if(existing!=null && !same(existing,actual))throw LanLocalDeferred("recovery_vault_identity_changed")
            if(existing==null) { row.put("vaultIdentity",JSONArray(actual.toList())).put("backupVault",name);save(row,"vault_prepared") }
            return value
        }catch(e: Exception) { value.close();throw e }
    }
    private suspend fun moveOriginal(parent: Int,name: String,row: JSONObject) {
        val expected=if(row.isNull("expectedHash"))null else row.getString("expectedHash")
        if(expected==null && !row.has("vaultIdentity")) { if(LanAtomicFiles.statLeaf(parent,name)!=null)throw LanLocalDeferred("target_appeared");return }
        vault(parent,row,true).use { recovery ->
            val old=LanAtomicFiles.statLeaf(recovery.fd,"original")
            if(old!=null) {
                ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(recovery.fd,"original",false,false)).use { original ->
                    if(identity(row,"backupIdentity")?.let { same(it,old) }!=true || hash(original)!=row.optString("backupHash",expected.orEmpty()))throw LanLocalDeferred("recovery_original_changed")
                };return
            }
            val raw=LanAtomicFiles.openLeaf(parent,name,true,false)
            if(raw<0) { if(expected!=null)throw LanLocalDeferred("local_changed_before_commit");return }
            ParcelFileDescriptor.adoptFd(raw).use { original ->
                val id=LanAtomicFiles.identity(original.fd)
                if(hash(original)!=expected)throw LanLocalDeferred("local_changed_before_commit")
                if(database.backupBytes()+id[2]>32L*1024*1024*1024)throw LanLocalDeferred("backup_byte_budget")
                row.put("backupIdentity",JSONArray(id.toList())).put("backupBytes",id[2]).put("backupHash",expected);save(row,"vault_prepared")
                currentCoroutineContext().ensureActive();currentRoot()
                try { LanAtomicFiles.moveIntoVault(parent,name,recovery.fd,id[0],id[1]) }
                catch(e: IOException) {
                    // A racing source is retained inside the vault, never overwritten or discarded.
                    LanAtomicFiles.statLeaf(recovery.fd,"original")?.let { moved ->
                        row.put("backupIdentity",JSONArray(moved.toList())).put("backupBytes",moved[2]);
                        ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(recovery.fd,"original",false,false)).use { row.put("backupHash",hash(it)) };save(row,"vault_old_moved")
                    };throw e
                }
                save(row,"vault_old_moved")
                if(hash(original)!=expected)throw LanLocalDeferred("local_changed_during_commit")
            }
        }
    }
    private suspend fun copyExclusive(parent: Int,name: String,part: File,row: JSONObject) {
        val known=identity(row,"targetIdentity")
        val existing=LanAtomicFiles.statLeaf(parent,name)
        val raw=if(existing!=null) {
            if(known==null || !same(known,existing))throw LanLocalDeferred("target_appeared")
            LanAtomicFiles.openLeaf(parent,name,true,false)
        }else {
            if(known!=null)throw LanLocalDeferred("owned_partial_missing")
            LanAtomicFiles.openLeaf(parent,name,true,true)
        }
        ParcelFileDescriptor.adoptFd(raw).use { target ->
            val id=LanAtomicFiles.identity(target.fd)
            if(id[2]>part.length())throw LanLocalDeferred("owned_partial_changed")
            if(known==null) { row.put("targetIdentity",JSONArray(id.toList()));save(row,"copying") }
            part.inputStream().use { input ->
                // Resume only an exact, unchanged prefix of this transfer; no truncation of public files.
                val a=ByteArray(128*1024);val b=ByteArray(a.size);var left=id[2]
                android.system.Os.lseek(target.fileDescriptor,0,android.system.OsConstants.SEEK_SET)
                while(left>0) { currentCoroutineContext().ensureActive();val n=minOf(left,a.size.toLong()).toInt();var at=0;while(at<n) { val got=input.read(a,at,n-at);if(got<=0)throw LanLocalDeferred("owned_partial_changed");at+=got };at=0;while(at<n) { val got=read(target,b,at,n-at);if(got<=0)throw LanLocalDeferred("owned_partial_changed");at+=got };if(!(0 until n).all { a[it]==b[it] })throw LanLocalDeferred("owned_partial_changed");left-=n }
                while(true) { currentCoroutineContext().ensureActive();currentRoot();val n=input.read(a);if(n<0)break;write(target,a,n) }
            }
            target.fileDescriptor.sync();if(row.optLong("modifiedNs")>0)LanAtomicFiles.setModified(target.fd,row.getLong("modifiedNs"))
            val current=LanAtomicFiles.statLeaf(parent,name)
            if(current==null || !same(id,current) || hash(target)!=row.getString("hash"))throw LanLocalDeferred("local_changed_during_commit")
            save(row,"committed")
        }
    }
    private suspend fun installConditional(path: String,part: File,row: JSONObject,beforeCommit: suspend ()->Unit) {
        if(part.length()>maxBytes || File(absoluteRoot).usableSpace<part.length()+256L*1024*1024 || database.stagingBytes(row.getString("operation"))+part.length()>16L*1024*1024*1024)throw LanLocalDeferred("staging_byte_budget")
        val p=parent(path,true) ?: throw LanLocalDeferred("storage_unavailable")
        p.use { directory ->
            val name=path.substringAfterLast('/');beforeCommit();currentCoroutineContext().ensureActive();currentRoot()
            if(!row.has("targetIdentity")) {
                val observed=observe(path);val actual=if(observed.isNull("hash"))null else observed.getString("hash")
                if(actual==row.getString("hash")) { save(row,"committed");return }
                if(actual!=if(row.isNull("expectedHash"))null else row.getString("expectedHash")) {
                    if(actual!=null || !row.has("backupIdentity") || !row.has("vaultIdentity"))throw LanLocalDeferred("local_changed_before_commit")
                }
                moveOriginal(directory.fd,name,row)
            }
            copyExclusive(directory.fd,name,part,row)
        }
    }
    suspend fun recover(row: JSONObject,part: File?=null): String? {
        if(row.optString("commitMode")=="conditional_copy" && row.optString("state") in setOf("vault_prepared","vault_old_moved","copying")) {
            val path=LanProtocol.relative(row.getString("path"));val current=observe(path);val actual=if(current.isNull("hash"))null else current.getString("hash");val desired=if(row.isNull("hash"))null else row.getString("hash")
            if(actual==desired) { save(row,"committed");return null }
            if(desired==null && actual==row.optString("expectedHash")) { delete(path,row) {};return null }
            if(part!=null && part.isFile && com.tinyfish.jeekmusic.data.tags.SafeTagWriter.sha256(part)==desired) {
                try { installConditional(path,part,row) {} }
                catch(e: LanLocalDeferred) { if(e.reason !in setOf("owned_partial_changed","owned_partial_missing","target_appeared","local_changed_before_commit"))throw e;save(row,"local_changed") }
                return null
            }
            return "conditional_copy_needs_recovery"
        }
        if(row.optString("state") !in setOf("prepared","old_moved"))return null
        val path=LanProtocol.relative(row.getString("path"));val p=parent(path) ?: return "transaction_destination_unknown"
        return p.use { parent ->
            val name=path.substringAfterLast('/');val op=row.getString("operation");val actual=observe(path).let { if(it.isNull("hash"))null else it.getString("hash") }
            val expected=if(row.isNull("expectedHash"))null else row.getString("expectedHash")
            val desired=if(row.isNull("hash"))null else row.getString("hash")
            if(actual==desired)save(row,"committed")
            else if(actual==expected)save(row,"interrupted")
            else if(actual==null && expected!=null) {
                val backupName=".jeek-sync-$op.bak";val fd=LanAtomicFiles.openLeaf(parent.fd,backupName,true,false);if(fd<0)return "transaction_backup_unavailable"
                ParcelFileDescriptor.adoptFd(fd).use { backup ->
                    val id=LanAtomicFiles.identity(backup.fd);val known=identity(row,"backupIdentity")
                    if(known==null || !same(known,id) || hash(backup)!=expected)return "transaction_backup_changed"
                    LanAtomicFiles.moveNoReplace(parent.fd,backupName,name,id[0],id[1]);row.remove("backupIdentity");row.remove("backupBytes");save(row,"interrupted")
                }
            } else return "transaction_local_changed"
            val stageName=".jeek-sync-$op.new";val stage=LanAtomicFiles.statLeaf(parent.fd,stageName);val known=identity(row,"stageIdentity")
            if(stage!=null && known!=null && same(stage,known))LanAtomicFiles.removeOwned(parent.fd,stageName,stage[0],stage[1])
            null
        }
    }
    suspend fun maintainBackup(row: JSONObject,purge: Boolean,part: File) {
        if(row.getString("mapping")!=mapping.getString("key") || purge && row.optString("state") !in setOf("acknowledged","recovered"))throw LanLocalDeferred("unconfirmed_backup_retained")
        val path=LanProtocol.relative(row.getString("path"));val p=parent(path) ?: throw LanLocalDeferred("storage_unavailable")
        p.use { parent ->
            val op=row.getString("operation");require(op.matches(Regex("[0-9a-f]{32}")))
            val backupName=".jeek-sync-$op.bak";val recovery=if(row.has("vaultIdentity"))vault(parent.fd,row,false) else null
            try { val fd=LanAtomicFiles.openLeaf(recovery?.fd ?: parent.fd,if(recovery!=null) "original" else backupName,true,false);if(fd<0)throw LanLocalDeferred("backup_unavailable")
            ParcelFileDescriptor.adoptFd(fd).use { backup ->
                val known=identity(row,"backupIdentity") ?: throw LanLocalDeferred("backup_identity_missing");val actual=LanAtomicFiles.identity(backup.fd)
                val original=row.optString("backupHash",row.getString("expectedHash"))
                if(!same(known,actual) || hash(backup)!=original)throw LanLocalDeferred("backup_changed")
                if(purge) { if(recovery!=null)LanAtomicFiles.removeVaultOriginal(recovery.fd,actual[0],actual[1]) else LanAtomicFiles.removeOwned(parent.fd,backupName,actual[0],actual[1]);row.remove("backupIdentity");row.remove("backupBytes");database.transaction(row);return }
                val live=observe(path);var expected=if(row.isNull("hash"))null else row.getString("hash");val present=if(live.isNull("hash"))null else live.getString("hash")
                if(present!=expected) {
                    val pending=row.optString("state") in setOf("vault_prepared","vault_old_moved","copying")
                    val owned=identity(row,"targetIdentity");val current=LanAtomicFiles.statLeaf(parent.fd,path.substringAfterLast('/'))
                    if(!pending || present!=null && (owned==null || current==null || !same(owned,current)))throw LanLocalDeferred("live_file_changed_since_backup")
                    expected=present
                }
                if(part.parentFile!!.usableSpace<actual[2]+256L*1024*1024)throw LanLocalDeferred("private_staging_byte_budget")
                android.system.Os.lseek(backup.fileDescriptor,0,android.system.OsConstants.SEEK_SET)
                FileOutputStream(part).use { output -> val buffer=ByteArray(128*1024);while(true) { currentCoroutineContext().ensureActive();val n=read(backup,buffer);if(n<=0)break;output.write(buffer,0,n) };output.fd.sync() }
                val restore=JSONObject().put("operation",UUID.randomUUID().toString().replace("-","")).put("mapping",mapping.getString("key")).put("scope",row.getString("scope")).put("path",path).put("kind","local_restore").put("expectedHash",expected ?: JSONObject.NULL).put("hash",original).put("size",actual[2]).put("state","created")
                install(path,part,restore) {};database.confirmed(restore)
                if(row.optString("state") in setOf("vault_prepared","vault_old_moved","copying"))save(row,"recovered")
            }
            }finally { recovery?.close() }
        }
    }
    override fun close()=root.close()
}
