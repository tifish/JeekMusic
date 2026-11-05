package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.launch
import kotlinx.coroutines.coroutineScope
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.MessageDigest
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class LanLocalFilesTest {
    @Test fun publicMetadataScanAndProbeReadZeroFileContentIncludingCue()=fixture(shared=true) { root,db,mapping ->
        File(root,"Album").mkdirs()
        java.io.RandomAccessFile(File(root,"Album/Large.wav"),"rw").use { it.setLength(32*1024*1024) }
        File(root,"Album/Sheet.cue").fixtureText("Do not open or parse this during metadata scan")
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val before=LanFileMetrics.hashBytes.get();val start=android.os.SystemClock.elapsedRealtime()
            repeat(2) {
                val rows=mutableListOf<JSONObject>();local.scan(listOf(""),{ rows+=it },{ fail("Unknown directory: $it") })
                assertEquals(2,rows.size);assertTrue(rows.all { it.getBoolean("exists") && !it.has("stamp") })
                assertTrue(local.observe("Album/Large.wav").getBoolean("exists"));assertFalse(local.observe("Missing.wav").getBoolean("exists"))
            }
            assertEquals(before,LanFileMetrics.hashBytes.get())
            println("METADATA_EVIDENCE bytes=33554432 rounds=2 hashBytes=0 elapsedMs=${android.os.SystemClock.elapsedRealtime()-start}")
        }
    }
    @Test fun confirmedBackupsRestoreAndPurgeOneReceiptWithoutAdvancingBaselines()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { fixtureText("first tone") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"A.wav",null,hash("first tone")).put("modifiedNs",1_700_000_000_123_000_000L)
            local.install("A.wav",part,first) {};db.confirmed(first)
            assertTrue(kotlin.math.abs(local.observe("A.wav").getLong("modifiedNs")-1_700_000_000_123_000_000L)<1_000_000)
            part.fixtureText("second tone");val second=row(mapping,"A.wav",hash("first tone"),hash("second tone"));local.install("A.wav",part,second) {};db.confirmed(second)
            local.maintainBackup(second,false,File(root.parentFile,"restore.part"));assertEquals("first tone",File(root,"A.wav").readText())
            local.maintainBackup(second,true,File(root.parentFile,"unused.part"));assertEquals("first tone",File(root,"A.wav").readText())
            assertFalse(File(root,".jeek-sync-${second.getString("operation")}.bak").exists());assertNull(db.baseline(mapping.getString("key"),"A.wav"))
            val stale=row(mapping,"B.wav",hash("old"),hash("new")).put("state","prepared")
            try { local.maintainBackup(stale,true,part);fail("Unconfirmed receipt was accepted") }catch(_: LanLocalDeferred) { }
        }
    }
    private fun time(text: String)=if(text=="first tone")1_700_000_000_123_000_000L else 1_700_000_000_000_000_000L+kotlin.math.abs(text.hashCode().toLong())*1000
    private fun hash(text: String)=LanFileMetrics.token(text.toByteArray().size.toLong(),time(text))
    private fun File.fixtureText(text: String) { writeText(text);LanFileMetrics.setModified(this,time(text)) }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun fixture(shared: Boolean=false,block: suspend (File,LanFileDatabase,JSONObject)->Unit)=runBlocking {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val parent=File(base.cacheDir,"local-files-${id()}").apply { mkdirs() }
        val context=object: ContextWrapper(base) { override fun getNoBackupFilesDir()=File(parent,"private").apply { mkdirs() } }
        val root=if(shared) { org.junit.Assume.assumeTrue(android.os.Environment.isExternalStorageManager());File(android.os.Environment.getExternalStorageDirectory(),"Documents/jeek-file-fixture-${id()}").apply { mkdirs() }.canonicalFile } else File(parent,"music").apply { mkdirs() }.canonicalFile
        val hashesBefore=LanFileMetrics.hashBytes.get()
        try { block(root,LanFileDatabase(context),JSONObject().put("key",UUID.randomUUID().toString()));assertEquals(hashesBefore,LanFileMetrics.hashBytes.get()) } finally { if(shared)root.deleteRecursively();parent.deleteRecursively() }
    }
    private fun row(mapping: JSONObject,path: String,expected: String?,desired: String?,kind: String="download")=JSONObject().put("operation",id()).put("mapping",mapping.getString("key")).put("scope","synthetic-scope").put("path",path).put("kind",kind).put("expectedStamp",expected ?: JSONObject.NULL).put("stamp",desired ?: JSONObject.NULL).put("size",(desired ?: expected)?.split(':')?.get(1)?.toLong() ?: 0L).put("modifiedNs",desired?.split(':')?.get(2)?.toLong() ?: 0L).put("state","created")
    @Test fun publicSharedStorageUsesExclusiveCopyAndOwnedRecoveryVaults()=fixture(shared=true) { root,db,mapping ->
        val part=File(root.parentFile,".jeek-test-part-${id()}").apply { fixtureText("old tone") }
        try { LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"Album/A.wav",null,hash("old tone"));local.install("Album/A.wav",part,first) {};db.confirmed(first)
            assertEquals("old tone",File(root,"Album/A.wav").readText())
            part.fixtureText("new tone");val second=row(mapping,"Album/A.wav",hash("old tone"),hash("new tone"));local.install("Album/A.wav",part,second) {};db.confirmed(second)
            assertEquals("new tone",File(root,"Album/A.wav").readText());assertTrue(second.has("backupIdentity"))
            local.maintainBackup(second,false,File(root,"restore.part"));assertEquals("old tone",File(root,"Album/A.wav").readText())
            local.maintainBackup(second,true,part)
            val deletion=row(mapping,"Album/A.wav",hash("old tone"),null,"delete_local");local.delete("Album/A.wav",deletion) {};db.confirmed(deletion);assertFalse(File(root,"Album/A.wav").exists())
            local.maintainBackup(deletion,false,File(root,"restore-2.part"));assertEquals("old tone",File(root,"Album/A.wav").readText())
            val race=row(mapping,"Race.wav",null,hash("old tone"));part.fixtureText("old tone")
            try { local.install("Race.wav",part,race) { File(root,"Race.wav").fixtureText("foreign") };fail("Racing public file overwritten") }catch(_: java.io.IOException) { }
            assertEquals("foreign",File(root,"Race.wav").readText())
        } }finally { part.delete() }
    }
    @Test fun publicCopyJournalResumesRecordedMetadataAndPreservesIndependentEdits()=fixture(shared=true) { root,db,mapping ->
        val bytes=ByteArray(65536) { (it%231).toByte() }
        val part=File(root,"private-fixture.part").apply { writeBytes(bytes) };val desired=LanFileMetrics.stamp(part)
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val target=File(root,"A.wav").apply { writeBytes(bytes.copyOfRange(0,12345)) }
            fun transaction(path: String): JSONObject {
                val row=row(mapping,path,null,desired).put("size",bytes.size).put("commitMode","conditional_copy").put("state","copying")
                android.os.ParcelFileDescriptor.adoptFd(LanAtomicFiles.openRoot(root.path)).use { p -> android.os.ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(p.fd,path,false,false)).use { file -> row.put("targetIdentity",org.json.JSONArray(LanAtomicFiles.identity(file.fd).toList())) } };db.transaction(row);return row
            }
            val first=transaction("A.wav");assertNull(local.recover(first,part));assertArrayEquals(bytes,target.readBytes());assertEquals("committed",first.getString("state"));assertNull(db.baseline(mapping.getString("key"),"A.wav"))
            File(root,"B.wav").writeBytes(bytes.copyOfRange(0,12345));val edited=transaction("B.wav");File(root,"B.wav").fixtureText("independent edit");assertNull(local.recover(edited,part));assertEquals("local_changed",edited.getString("state"));assertEquals("independent edit",File(root,"B.wav").readText())
            val uncheckpointed=File(root,"C.wav").apply { writeBytes(bytes.copyOfRange(0,4096)) };val pending=transaction("C.wav")
            uncheckpointed.appendBytes(bytes.copyOfRange(4096,8192))
            assertNull(local.recover(pending,part));assertEquals("local_changed",pending.getString("state"));assertEquals(8192,uncheckpointed.length().toInt())
        }
    }
    @Test fun normalCancellationCheckpointsPublicPartialAndResumes()=fixture(shared=true) { root,db,mapping ->
        val bytes=ByteArray(4*1024*1024) { (it%231).toByte() }
        val part=File(root,"private-fixture.part").apply { writeBytes(bytes) };val desired=LanFileMetrics.stamp(part)
        val target=File(root,"Cancel.wav");var cancelOnce=true;lateinit var writer: kotlinx.coroutines.Job
        val transaction=row(mapping,"Cancel.wav",null,desired).put("size",bytes.size).put("modifiedNs",desired.split(':')[2].toLong())
        LanLocalFiles(root.path,mapping,db,true) {
            if(cancelOnce && target.length()>0) { cancelOnce=false;writer.cancel() }
        }.use { local ->
            coroutineScope { writer=launch { local.install("Cancel.wav",part,transaction) {} };writer.join() }
            assertTrue(writer.isCancelled);assertEquals("copying",transaction.getString("state"))
            assertEquals(LanFileMetrics.stamp(target),transaction.getString("copyStamp"))
            assertNull(local.recover(transaction,part));assertEquals("committed",transaction.getString("state"));assertArrayEquals(bytes,target.readBytes())
        }
    }
    @Test fun directInstallSameContentRecoveryAndDeletionKeepUserBackups()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { fixtureText("new tone") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"Album/A.wav",null,hash("new tone"));local.install("Album/A.wav",part,first) {}
            assertEquals("new tone",File(root,"Album/A.wav").readText());assertFalse(root.listFiles()!!.any { it.name.startsWith("JeekMusic-") })
            part.fixtureText("changed tone");val next=row(mapping,"Album/A.wav",hash("new tone"),hash("changed tone"));local.install("Album/A.wav",part,next) {}
            assertEquals("changed tone",File(root,"Album/A.wav").readText());assertEquals("new tone",File(root,"Album/.jeek-sync-${next.getString("operation")}.bak").readText())
            val deletion=row(mapping,"Album/A.wav",hash("changed tone"),null,"delete");local.delete("Album/A.wav",deletion) {}
            assertFalse(File(root,"Album/A.wav").exists());assertEquals("changed tone",File(root,"Album/.jeek-sync-${deletion.getString("operation")}.bak").readText())
        }
        LanLocalFiles(root.path,mapping,db,false).use { assertTrue(it.observe("Album/A.wav").isNull("stamp")) }
    }
    @Test fun cancellationAndConcurrentChangesDoNotOverwriteOrAdvanceJournal()=fixture { root,db,mapping ->
        File(root,"A.wav").fixtureText("original");val part=File(root.parentFile,"part").apply { fixtureText("remote") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val cancelled=row(mapping,"A.wav",hash("original"),hash("remote"))
            try { local.install("A.wav",part,cancelled) { throw java.io.IOException("synthetic cancellation before commit") };fail("Committed") }catch(_: java.io.IOException) { }
            assertEquals("original",File(root,"A.wav").readText())
            val raced=row(mapping,"A.wav",hash("original"),hash("remote"))
            try { local.install("A.wav",part,raced) { File(root,"A.wav").fixtureText("local edit") };fail("Overwrote local edit") }catch(_: java.io.IOException) { }
            assertEquals("local edit",File(root,"A.wav").readText())
        }
    }
    @Test fun rootReplacementAndInterruptedRenameAreDistinguishedFromDeletion()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { fixtureText("remote") };val transaction=row(mapping,"A.wav",hash("original"),hash("remote"))
        File(root,"A.wav").fixtureText("original")
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            try { local.install("A.wav",part,transaction) { throw java.io.IOException("before rename") } }catch(_: java.io.IOException) { }
        }
        // Synthetic hard-stop point after the old name moved. Preserve the recorded inode/hash.
        File(root,"A.wav").renameTo(File(root,".jeek-sync-${transaction.getString("operation")}.bak"))
        val backup=File(root,".jeek-sync-${transaction.getString("operation")}.bak")
        android.os.ParcelFileDescriptor.open(backup,android.os.ParcelFileDescriptor.MODE_READ_ONLY).use { p -> transaction.put("backupIdentity",org.json.JSONArray(LanAtomicFiles.identity(p.fd).toList())) }
        transaction.put("state","old_moved");db.transaction(transaction)
        LanLocalFiles(root.path,mapping,db,false).use { assertNull(it.recover(transaction));assertEquals("original",File(root,"A.wav").readText()) }
        File(root,".jeek-sync-directory-id").delete()
        assertThrows(java.io.IOException::class.java) { LanLocalFiles(root.path,mapping,db,false) }
    }
    @Test fun wideDirectoryEnumerationIsPagedAndDoesNotFollowLinks()=fixture { root,db,mapping ->
        for(i in 0 until 405)File(root,"$i.lrc").fixtureText("lyrics")
        val outside=File(root.parentFile,"outside").apply { mkdirs() };File(outside,"private.lrc").fixtureText("not selected")
        android.system.Os.symlink(outside.path,File(root,"Linked").path)
        try { LanLocalFiles(root.path,mapping,db,true).use { local -> var count=0;local.scan(listOf(""),{ count++ },{});assertEquals(405,count) } }
        finally { File(root,"Linked").delete() }
    }
}
