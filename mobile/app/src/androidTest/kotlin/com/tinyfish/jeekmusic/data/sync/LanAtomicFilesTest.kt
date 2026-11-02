package com.tinyfish.jeekmusic.data.sync

import android.os.ParcelFileDescriptor
import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class LanAtomicFilesTest {
    private fun fixture(external: Boolean=false,block: (File)->Unit) {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val parent=if(external) { org.junit.Assume.assumeTrue(android.os.Environment.isExternalStorageManager());File(android.os.Environment.getExternalStorageDirectory(),"Documents").apply { mkdirs() } } else context.cacheDir
        val root=File(parent,"atomic-sync-${UUID.randomUUID()}").apply { mkdirs() }.canonicalFile
        try { block(root) } finally { root.deleteRecursively() }
    }
    private fun parent(root: File,block: (Int)->Unit) = ParcelFileDescriptor.adoptFd(LanAtomicFiles.openRoot(root.path)).use { r ->
        ParcelFileDescriptor.adoptFd(LanAtomicFiles.openParent(r.fd,"",false)).use { p -> block(p.fd) }
    }
    @Test fun noOverwriteAndRecoverableRenameOnRealPrivateAndExternalStorage() {
        for(external in listOf(false,true)) fixture(external) { root -> parent(root) { fd ->
            File(root,"A.wav").writeText("original")
            File(root,".jeek-sync-test.new").writeText("new")
            ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(fd,"A.wav",true,false)).use { old ->
                val identity=LanAtomicFiles.identity(old.fd)
                try { LanAtomicFiles.moveNoReplace(fd,"A.wav",".jeek-sync-test.bak",identity[0],identity[1]) }
                catch(e: IOException) {
                    if(!external || e.message!="rename_noreplace_unsupported")throw e
                    assertEquals("original",File(root,"A.wav").readText());return@parent
                }
                assertFalse(File(root,"A.wav").exists());assertEquals("original",File(root,".jeek-sync-test.bak").readText())
                ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(fd,".jeek-sync-test.new",true,false)).use { stage ->
                    val id=LanAtomicFiles.identity(stage.fd)
                    File(root,"A.wav").writeText("racing local file")
                    assertThrows(IOException::class.java) { LanAtomicFiles.moveNoReplace(fd,".jeek-sync-test.new","A.wav",id[0],id[1]) }
                    assertEquals("racing local file",File(root,"A.wav").readText());assertEquals("new",File(root,".jeek-sync-test.new").readText())
                }
            }
        } }
    }
    @Test fun linksUnknownParentsAndWrongIdentityNeverBecomeDeletes() = fixture { root ->
        val outside=File(root.parentFile,"atomic-outside-${UUID.randomUUID()}").apply { mkdirs() }
        try {
            File(outside,"secret.wav").writeText("retained")
            Os.symlink(outside.path,File(root,"Linked").path);Os.symlink(File(outside,"secret.wav").path,File(root,"link.wav").path)
            parent(root) { fd ->
                assertThrows(IOException::class.java) { LanAtomicFiles.openParent(fd,"Linked",false) }
                assertThrows(IOException::class.java) { LanAtomicFiles.statLeaf(fd,"link.wav") }
                assertThrows(IOException::class.java) { LanAtomicFiles.openLeaf(fd,"link.wav",false,false) }
                assertEquals(-1,LanAtomicFiles.openParent(fd,"Missing",false))
                assertThrows(IOException::class.java) { LanAtomicFiles.openParent(fd,"../escape",true) }
                File(root,"A.wav").writeText("keep")
                assertThrows(IOException::class.java) { LanAtomicFiles.moveNoReplace(fd,"A.wav",".jeek-sync-test.bak",0,0) }
                assertEquals("keep",File(root,"A.wav").readText())
            }
            assertEquals("retained",File(outside,"secret.wav").readText())
        } finally { File(root,"Linked").delete();File(root,"link.wav").delete();outside.deleteRecursively() }
    }
    @Test fun supplementaryUnicodeBusyFileAndOwnedCleanup() = fixture { root -> parent(root) { fd ->
        val name="片段🎵.wav";File(root,name).writeText("tone")
        ParcelFileDescriptor.adoptFd(LanAtomicFiles.openLeaf(fd,name,true,false)).use { file ->
            assertEquals(4,LanAtomicFiles.identity(file.fd)[2])
            assertThrows(IOException::class.java) { LanAtomicFiles.openLeaf(fd,name,true,false) }
            val id=LanAtomicFiles.identity(file.fd)
            assertThrows(IOException::class.java) { LanAtomicFiles.removeOwned(fd,name,id[0],id[1]) }
            LanAtomicFiles.moveNoReplace(fd,name,".jeek-sync-test.new",id[0],id[1])
            assertThrows(IOException::class.java) { LanAtomicFiles.removeOwned(fd,".jeek-sync-test.new",0,0) }
            LanAtomicFiles.removeOwned(fd,".jeek-sync-test.new",id[0],id[1]);assertFalse(File(root,".jeek-sync-test.new").exists())
        }
    } }
}
