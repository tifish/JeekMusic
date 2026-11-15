package com.tinyfish.jeekmusic.data.sync

import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.io.RandomAccessFile
import java.util.Base64

class LanAudioEnvelopeTest {
    @Test fun peerLayoutsCannotReuseOutsideTheObservedSourceOrWithoutAnExactDigest() {
        val reply=org.json.JSONObject().put("supported",true).put("format","wav").put("start",44).put("length",1048576).put("digest","a".repeat(64))
        assertNotNull(LanAudioEnvelope.from(reply,1048620))
        assertNull(LanAudioEnvelope.from(reply,1048619))
        assertNull(LanAudioEnvelope.from(org.json.JSONObject(reply.toString()).put("start",Long.MAX_VALUE),1048620))
        val unverified=LanAudioEnvelope.from(org.json.JSONObject(reply.toString()).put("digest","invalid"),1048620)!!
        assertFalse(unverified.matches(unverified))
        assertFalse(LanAudioEnvelope("wav",44,1048576,"a".repeat(64)).matches(LanAudioEnvelope("flac",44,1048576,"a".repeat(64))))
    }
    @Test fun sharedLayoutsAndExactMediaDigestsAgreeAcrossTagRelocation() {
        val cases=JSONArray(File(System.getProperty("jeek.testcases"),"audio-envelope.json").readText())
        val directory=kotlin.io.path.createTempDirectory("audio-layout-").toFile()
        try {
            for(i in 0 until cases.length()) {
                val row=cases.getJSONObject(i);val path=row.getString("path")
                val a=File(directory,"source").apply { writeBytes(Base64.getDecoder().decode(row.getString("source"))) }
                val b=File(directory,"target").apply { writeBytes(Base64.getDecoder().decode(row.getString("target"))) }
                RandomAccessFile(a,"r").use { source -> RandomAccessFile(b,"rw").use { target ->
                    val left=LanAudioEnvelope.parse(source.channel,path);val right=LanAudioEnvelope.parse(target.channel,path)
                    if(row.getInt("start")<0) { assertNull(left);assertNull(right) }
                    else {
                        assertNotNull(left);assertNotNull(right)
                        assertEquals(row.getLong("start"),left!!.start);assertEquals(row.getLong("targetStart"),right!!.start);assertEquals(row.getLong("length"),left.length)
                        val digest=LanAudioEnvelope.digest(source.channel,left.start,left.length)
                        assertEquals(digest,LanAudioEnvelope.digest(target.channel,right.start,right.length))
                        target.seek(right.start);target.writeByte(0)
                        assertNotEquals(digest,LanAudioEnvelope.digest(target.channel,right.start,right.length))
                    }
                } }
            }
        }finally { directory.deleteRecursively() }
    }
}
