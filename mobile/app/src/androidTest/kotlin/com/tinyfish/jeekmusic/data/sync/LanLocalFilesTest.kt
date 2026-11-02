package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.MessageDigest
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class LanLocalFilesTest {
    @Test fun confirmedBackupsRestoreAndPurgeOneReceiptWithoutAdvancingBaselines()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { writeText("first tone") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"A.wav",null,hash("first tone")).put("modifiedNs",1_700_000_000_123_000_000L)
            local.install("A.wav",part,first) {};db.confirmed(first)
            assertTrue(kotlin.math.abs(local.observe("A.wav").getLong("modifiedNs")-1_700_000_000_123_000_000L)<1_000_000)
            part.writeText("second tone");val second=row(mapping,"A.wav",hash("first tone"),hash("second tone"));local.install("A.wav",part,second) {};db.confirmed(second)
            local.maintainBackup(second,false,File(root.parentFile,"restore.part"));assertEquals("first tone",File(root,"A.wav").readText())
            local.maintainBackup(second,true,File(root.parentFile,"unused.part"));assertEquals("first tone",File(root,"A.wav").readText())
            assertFalse(File(root,".jeek-sync-${second.getString("operation")}.bak").exists());assertNull(db.baseline(mapping.getString("key"),"A.wav"))
            val stale=row(mapping,"B.wav",hash("old"),hash("new")).put("state","prepared")
            try { local.maintainBackup(stale,true,part);fail("Unconfirmed receipt was accepted") }catch(_: LanLocalDeferred) { }
        }
    }
    private fun hash(text: String)=MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }
    private fun id()=UUID.randomUUID().toString().replace("-","")
    private fun fixture(shared: Boolean=false,block: suspend (File,LanFileDatabase,JSONObject)->Unit)=runBlocking {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val parent=File(base.cacheDir,"local-files-${id()}").apply { mkdirs() }
        val context=object: ContextWrapper(base) { override fun getNoBackupFilesDir()=File(parent,"private").apply { mkdirs() } }
        val root=if(shared) { org.junit.Assume.assumeTrue(android.os.Environment.isExternalStorageManager());File(android.os.Environment.getExternalStorageDirectory(),"Documents/jeek-file-fixture-${id()}").apply { mkdirs() }.canonicalFile } else File(parent,"music").apply { mkdirs() }.canonicalFile
        try { block(root,LanFileDatabase(context),JSONObject().put("key",UUID.randomUUID().toString())) } finally { if(shared)root.deleteRecursively();parent.deleteRecursively() }
    }
    private fun row(mapping: JSONObject,path: String,expected: String?,desired: String?,kind: String="download")=JSONObject().put("operation",id()).put("mapping",mapping.getString("key")).put("scope","synthetic-scope").put("path",path).put("kind",kind).put("expectedHash",expected ?: JSONObject.NULL).put("hash",desired ?: JSONObject.NULL).put("size",8).put("state","created")
    @Test fun publicSharedStorageUsesExclusiveCopyAndOwnedRecoveryVaults()=fixture(shared=true) { root,db,mapping ->
        val part=File(root.parentFile,".jeek-test-part-${id()}").apply { writeText("old tone") }
        try { LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"Album/A.wav",null,hash("old tone"));local.install("Album/A.wav",part,first) {};db.confirmed(first)
            assertEquals("old tone",File(root,"Album/A.wav").readText())
            part.writeText("new tone");val second=row(mapping,"Album/A.wav",hash("old tone"),hash("new tone"));local.install("Album/A.wav",part,second) {};db.confirmed(second)
            assertEquals("new tone",File(root,"Album/A.wav").readText());assertTrue(second.has("backupIdentity"))
            local.maintainBackup(second,false,File(root,"restore.part"));assertEquals("old tone",File(root,"Album/A.wav").readText())
            local.maintainBackup(second,true,part)
            val deletion=row(mapping,"Album/A.wav",hash("old tone"),null,"delete_local");local.delete("Album/A.wav",deletion) {};db.confirmed(deletion);assertFalse(File(root,"Album/A.wav").exists())
            local.maintainBackup(deletion,false,File(root,"restore-2.part"));assertEquals("old tone",File(root,"Album/A.wav").readText())
            val race=row(mapping,"Race.wav",null,hash("old tone"));part.writeText("old tone")
            try { local.install("Race.wav",part,race) { File(root,"Race.wav").writeText("foreign") };fail("Racing public file overwritten") }catch(_: java.io.IOException) { }
            assertEquals("foreign",File(root,"Race.wav").readText())
        } }finally { part.delete() }
    }
    @Test fun publicCopyJournalResumesOnlyItsExactPrefixAndPreservesIndependentEdits()=fixture(shared=true) { root,db,mapping ->
        val bytes=ByteArray(65536) { (it%231).toByte() };val desired=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
        val part=File(root,"private-fixture.part").apply { writeBytes(bytes) }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val target=File(root,"A.wav").apply { writeBytes(bytes.copyOfRange(0,12345)) }
            fun transaction(path: String): JSONObject {
                val row=row(mapping,path,null,desired).put("size",bytes.size).put("commitMode","conditional_copy").put("state","copying")
                android.os.ParcelFileDescriptor.adoptFd(LanAtomicFiles.openRoot(root.path)).use { p -> android.os.ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(p.fd,path,false,false)).use { file -> row.put("targetIdentity",org.json.JSONArray(LanAtomicFiles.identity(file.fd).toList())) } };db.transaction(row);return row
            }
            val first=transaction("A.wav");assertNull(local.recover(first,part));assertArrayEquals(bytes,target.readBytes());assertEquals("committed",first.getString("state"));assertNull(db.baseline(mapping.getString("key"),"A.wav"))
            File(root,"B.wav").writeText("independent edit");val edited=transaction("B.wav");assertNull(local.recover(edited,part));assertEquals("local_changed",edited.getString("state"));assertEquals("independent edit",File(root,"B.wav").readText())
        }
    }
    @Test fun directInstallSameContentRecoveryAndDeletionKeepUserBackups()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { writeText("new tone") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val first=row(mapping,"Album/A.wav",null,hash("new tone"));local.install("Album/A.wav",part,first) {}
            assertEquals("new tone",File(root,"Album/A.wav").readText());assertFalse(root.listFiles()!!.any { it.name.startsWith("JeekMusic-") })
            part.writeText("changed tone");val next=row(mapping,"Album/A.wav",hash("new tone"),hash("changed tone"));local.install("Album/A.wav",part,next) {}
            assertEquals("changed tone",File(root,"Album/A.wav").readText());assertEquals("new tone",File(root,"Album/.jeek-sync-${next.getString("operation")}.bak").readText())
            val deletion=row(mapping,"Album/A.wav",hash("changed tone"),null,"delete");local.delete("Album/A.wav",deletion) {}
            assertFalse(File(root,"Album/A.wav").exists());assertEquals("changed tone",File(root,"Album/.jeek-sync-${deletion.getString("operation")}.bak").readText())
        }
        LanLocalFiles(root.path,mapping,db,false).use { assertTrue(it.observe("Album/A.wav").isNull("hash")) }
    }
    @Test fun cancellationAndConcurrentChangesDoNotOverwriteOrAdvanceJournal()=fixture { root,db,mapping ->
        File(root,"A.wav").writeText("original");val part=File(root.parentFile,"part").apply { writeText("remote") }
        LanLocalFiles(root.path,mapping,db,true).use { local ->
            val cancelled=row(mapping,"A.wav",hash("original"),hash("remote"))
            try { local.install("A.wav",part,cancelled) { throw java.io.IOException("synthetic cancellation before commit") };fail("Committed") }catch(_: java.io.IOException) { }
            assertEquals("original",File(root,"A.wav").readText())
            val raced=row(mapping,"A.wav",hash("original"),hash("remote"))
            try { local.install("A.wav",part,raced) { File(root,"A.wav").writeText("local edit") };fail("Overwrote local edit") }catch(_: java.io.IOException) { }
            assertEquals("local edit",File(root,"A.wav").readText())
        }
    }
    @Test fun rootReplacementAndInterruptedRenameAreDistinguishedFromDeletion()=fixture { root,db,mapping ->
        val part=File(root.parentFile,"part").apply { writeText("remote") };val transaction=row(mapping,"A.wav",hash("original"),hash("remote"))
        File(root,"A.wav").writeText("original")
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
        for(i in 0 until 405)File(root,"$i.lrc").writeText("lyrics")
        val outside=File(root.parentFile,"outside").apply { mkdirs() };File(outside,"private.lrc").writeText("not selected")
        android.system.Os.symlink(outside.path,File(root,"Linked").path)
        try { LanLocalFiles(root.path,mapping,db,true).use { local -> var count=0;local.scan(listOf(""),{ count++ },{});assertEquals(405,count) } }
        finally { File(root,"Linked").delete() }
    }
}
