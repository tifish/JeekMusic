package com.tinyfish.jeekmusic.data.sync

import android.content.ContextWrapper
import android.graphics.Bitmap
import android.util.Base64
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.tinyfish.jeekmusic.MainActivity
import com.tinyfish.jeekmusic.data.library.PlayingFiles
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.UUID

/** Two actual Windows TLS servers, real pairing and selector UI; synthetic private music only. */
@RunWith(AndroidJUnit4::class)
class LanMultiPeerTest {
    @Test fun pairingRoutingOfflineIsolationAndRealSelector() = runBlocking {
        check(android.os.Build.HARDWARE in setOf("ranchu", "goldfish"))
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val raw = InstrumentationRegistry.getArguments().getString("jeekMultiPeerFixture") ?: error("Explicit isolated fixture required")
        val config = JSONObject(String(Base64.decode(raw, Base64.NO_WRAP), Charsets.UTF_8))
        require(config.getBoolean("isolated"))
        val invitations = config.getJSONArray("invitations").let { a -> List(a.length()) { a.getString(it) } }
        require(invitations.size == 2 && invitations.all { LanInvitation.parse(it).host == "127.0.0.1" })
        val context = instrumentation.targetContext
        val service = EntryPointAccessors.fromApplication(context, LanSyncWorker.Dependencies::class.java).lan()
        service.cancel(); service.awaitSync()
        val saved = service.store.read()
        val parent = File(context.cacheDir, "multi-peer-${UUID.randomUUID()}").apply { mkdirs() }
        val isolated = object : ContextWrapper(context) {
            override fun getFilesDir() = File(parent, "private").apply { mkdirs() }
            override fun getNoBackupFilesDir() = File(parent, "database").apply { mkdirs() }
        }
        val fileDb = LanFileDatabase(isolated)
        val ui = instrumentation.uiAutomation
        fun find(tag: String): AccessibilityNodeInfo? {
            fun visit(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
                if (node.viewIdResourceName == tag || node.viewIdResourceName?.endsWith("/$tag") == true) return node
                repeat(node.childCount) { i -> node.getChild(i)?.let { visit(it)?.let { found -> return found } } }
                return null
            }
            return ui.rootInActiveWindow?.let(::visit)
        }
        suspend fun tap(tag: String) {
            withTimeout(15_000) {
                while (true) {
                    var node = find(tag)
                    while (node?.isClickable == false) node = node.parent
                    if (node?.isEnabled == true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) break
                    delay(100)
                }
            }
            delay(300)
        }
        fun screenshot(name: String) {
            val destination = File(context.cacheDir, "multi-peer-ui").apply { mkdirs() }
            val bitmap = ui.takeScreenshot() ?: error("Screenshot unavailable")
            File(destination, "$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()
        }
        try {
            service.store.update(false) { state ->
                state.put("peers", JSONArray()).put("primaryPeerId", "").put("selectedPeerId", "").put("mappings", JSONArray()).put("autoCancelled", true)
                state.remove("connection"); state.remove("manualPending"); state.remove("manualPeerId")
            }
            service.pair(invitations[0])
            val first = service.store.selectedPeerId()
            val firstConnection = service.store.connection(first)
            val firstRoots = service.roots(first)
            assertEquals(1, firstRoots.size)
            service.pair(invitations[1])
            val second = service.store.selectedPeerId()
            assertNotEquals(first, second)
            assertEquals(2, service.store.peers().size)
            assertEquals(firstConnection.getString("token"), service.store.connection(first).getString("token"))
            assertEquals(first, service.store.primaryPeerId())
            val secondRoots = service.roots(second)
            val roots = listOf(firstRoots.single(), secondRoots.single())
            val localRoots = listOf(File(parent, "pc-a").apply { mkdirs() }.canonicalFile, File(parent, "pc-b").apply { mkdirs() }.canonicalFile)
            val mappings = roots.mapIndexed { index, root -> JSONObject().put("peerId", root.getString("peerId"))
                .put("rootId", root.getString("rootId")).put("key", UUID.randomUUID().toString()).put("selection", JSONArray(listOf("")))
                .put("layoutVersion", 2).put("volume", "fixture").put("relative", "$index/").put("enabled", false).put("name", "PC ${index + 1}")
                .put("treeUri", "content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FMultiPeer$index")
                .put("documentId", "primary:Documents/MultiPeer$index") }
            for (mapping in mappings) service.store.saveMapping(mapping)
            // The production reconciler and explicit peer credentials, with private test IO roots.
            for (index in mappings.indices) {
                val mapping = mappings[index]
                val synchronizer = LanFileSynchronizer(isolated, fileDb, PlayingFiles()) { m, pin -> LanLocalFiles(localRoots[index].path, m, fileDb, pin) }
                LanProtocol(service.store.connectionFor(mapping), service.networkPolicy).use { client ->
                    val report = synchronizer.synchronize(mapping, client, {}, { _, _ -> }, { _, _, _ -> }, {})
                    assertEquals(report.deferred.toString(), 0, report.deferredCount)
                    assertEquals(0, report.conflictCount)
                }
                assertTrue(File(localRoots[index], "same.lrc").exists())
                File(localRoots[index], "phone.lrc").writeText("PHONE-${index+1}")
                LanProtocol(service.store.connectionFor(mapping), service.networkPolicy).use { client ->
                    val report = synchronizer.synchronize(mapping, client, {}, { _, _ -> }, { _, _, _ -> }, {})
                    assertEquals(report.deferred.toString(), 0, report.deferredCount)
                    assertEquals(0, report.conflictCount)
                }
            }
            assertEquals("PC-A synthetic lyrics", File(localRoots[0], "same.lrc").readText())
            assertEquals("PC-B synthetic lyrics", File(localRoots[1], "same.lrc").readText())
            // Changing the visible peer does not reroute a captured mapping's request.
            service.selectPeer(second)
            assertTrue(service.pollFileRevision(mappings[0]).isNotEmpty())
            val secondPort = service.store.connection(second).getInt("port")
            service.store.updatePeer(second) { it.put("port", 9) }
            assertTrue(runCatching { service.roots(second) }.isFailure)
            assertFalse(service.peerReady(second))
            assertEquals(first, service.roots(first).single().getString("peerId"))
            assertTrue(service.peerReady(first))
            ActivityScenario.launch(MainActivity::class.java).use {
                tap("open_settings"); tap("settings_group_devices")
                tap("sync_peer_$second"); screenshot("second-offline")
                assertEquals(second, service.store.selectedPeerId())
                tap("sync_peer_$first"); screenshot("first-selected")
                assertEquals(first, service.store.selectedPeerId())
                assertEquals(2, service.store.mappings().size)
            }
            service.store.updatePeer(second) { it.put("port", secondPort) }
            service.forgetPeer(second)
            assertEquals(first, service.store.peers().single().getString("peerId"))
            assertEquals(firstConnection.getString("token"), service.store.connection(first).getString("token"))
            println("MULTI_PEER_EVIDENCE twoWindowsTlsServers=true differentSamePathContents=true selectorUi=true offlineIsolation=true originalCredentialPreserved=true privateFileIo=true")
        } catch(e: Throwable) { screenshot("failure");throw e }
        finally {
            service.cancel(); service.awaitSync()
            service.store.update(false) { state -> state.keys().asSequence().toList().forEach(state::remove); saved.keys().forEach { state.put(it, saved.get(it)) } }
            fileDb.close(); parent.deleteRecursively()
        }
    }
}
