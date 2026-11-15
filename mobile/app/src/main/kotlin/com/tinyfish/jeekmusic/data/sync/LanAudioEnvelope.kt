package com.tinyfish.jeekmusic.data.sync

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.channels.FileChannel
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong
import org.json.JSONObject

/** One contiguous, bounded media region; ambiguous containers keep the full-file path. */
internal data class LanAudioEnvelope(val format: String,val start: Long,val length: Long,val digest: String?=null) {
    val end get()=Math.addExact(start,length)
    fun matches(other: LanAudioEnvelope)=format==other.format && length==other.length && digest!=null && digest==other.digest
    companion object {
        val hashedBytes=AtomicLong()
        fun supports(path: String)=path.substringAfterLast('.').lowercase() in setOf("mp3","flac","wav","m4a","mp4")
        fun from(row: JSONObject,fileSize: Long): LanAudioEnvelope? {
            if(!row.optBoolean("supported"))return null
            val start=row.optLong("start",-1);val length=row.optLong("length",-1)
            if(fileSize !in 0..8L*1024*1024*1024 || length<262144 || start<0 || length>fileSize || start>fileSize-length || fileSize-length>32L*1024*1024)return null
            return LanAudioEnvelope(row.getString("format"),start,length,row.optString("digest").takeIf { it.matches(Regex("[0-9a-f]{64}")) })
        }
        fun parse(input: FileChannel,path: String): LanAudioEnvelope? {
            if(!supports(path) || input.size()<12)return null
            return try {
                val size=input.size()
                fun bytes(p: Long,n: Int): ByteArray { require(p>=0 && p<=size-n);input.position(p);val b=ByteArray(n);val out=ByteBuffer.wrap(b);while(out.hasRemaining())require(input.read(out)>0);return b }
                fun text(b: ByteArray,p: Int,s: String)=b.size>=p+s.length && s.indices.all { b[p+it].toInt() and 255==s[it].code }
                fun u32(b: ByteArray,p: Int,little: Boolean=false)=ByteBuffer.wrap(b,p,4).order(if(little)ByteOrder.LITTLE_ENDIAN else ByteOrder.BIG_ENDIAN).int.toLong() and 0xffffffffL
                fun result(format: String,start: Long,length: Long)=if(length>0 && start>=0 && start<=size-length && size-length<=32L*1024*1024)LanAudioEnvelope(format,start,length) else null
                val head=bytes(0,12)
                when(path.substringAfterLast('.').lowercase()) {
                    "mp3" -> {
                        var start=0L;var end=size
                        if(text(head,0,"ID3")) {
                            if(head[3].toInt() !in 2..4 || head[4].toInt()!=0 || (6..9).any { head[it].toInt() and 128!=0 })return null
                            start=10L+(6..9).fold(0) { n,i->(n shl 7) or head[i].toInt() }
                            if(head[3].toInt()==4 && head[5].toInt() and 16!=0) { if(!text(bytes(start,3),0,"3DI"))return null;start+=10 }
                            if(start>32L*1024*1024)return null
                        }
                        if(end>=128 && text(bytes(end-128,3),0,"TAG"))end-=128
                        if(end>=32 && text(bytes(end-32,8),0,"APETAGEX"))return null
                        val audio=bytes(start,2);if(audio[0].toInt() and 255!=255 || audio[1].toInt() and 224!=224)return null
                        result("mp3",start,end-start)
                    }
                    "flac" -> {
                        if(!text(head,0,"fLaC"))return null
                        var p=4L;var media: LanAudioEnvelope?=null
                        for(i in 1..4096) {
                            val b=bytes(p,4);val n=((b[1].toInt() and 255) shl 16) or ((b[2].toInt() and 255) shl 8) or (b[3].toInt() and 255)
                            if(i==1 && (b[0].toInt() and 127!=0 || n!=34) || b[0].toInt() and 127==127)return null
                            p=Math.addExact(p,4L+n);if(p>size || p>32L*1024*1024)return null
                            if(b[0].toInt() and 128!=0) { media=result("flac",p,size-p);break }
                        };media
                    }
                    "wav" -> {
                        if(!text(head,0,"RIFF") || !text(head,8,"WAVE") || u32(head,4,true)+8!=size)return null
                        var p=12L;var media: LanAudioEnvelope?=null;var format=false;var count=0
                        while(p<size && ++count<=100000) {
                            val b=bytes(p,8);val n=u32(b,4,true);val end=Math.addExact(p,8+n);if(end>size)return null
                            if(text(b,0,"fmt "))format=true
                            if(text(b,0,"data")) { if(media!=null)return null;media=result("wav",p+8,n) }
                            p=Math.addExact(end,n and 1)
                        };if(p==size && format)media else null
                    }
                    "m4a","mp4" -> {
                        if(!text(head,4,"ftyp"))return null
                        var p=0L;var count=0;var media: LanAudioEnvelope?=null
                        while(p<size && ++count<=100000) {
                            val b=bytes(p,8);var n=u32(b,0);var header=8
                            if(n==1L) { n=ByteBuffer.wrap(bytes(p+8,8)).long;header=16 }else if(n==0L)n=size-p
                            if(n<header || n>size-p)return null
                            if(text(b,4,"mdat")) { if(media!=null)return null;media=result("mp4",p+header,n-header) }
                            p=Math.addExact(p,n)
                        };if(p==size)media else null
                    }
                    else -> null
                }
            }catch(_: IllegalArgumentException) { null }catch(_: ArithmeticException) { null }
        }
        fun digest(input: FileChannel,start: Long,length: Long,check: ()->Unit={}): String {
            require(start>=0 && length>=0 && start<=input.size()-length)
            val hash=MessageDigest.getInstance("SHA-256");val buffer=ByteBuffer.allocate(131072);input.position(start);var remaining=length
            while(remaining>0) { check();buffer.clear();buffer.limit(minOf(buffer.capacity().toLong(),remaining).toInt());val n=input.read(buffer);require(n>0);hash.update(buffer.array(),0,n);hashedBytes.addAndGet(n.toLong());remaining-=n }
            return hash.digest().joinToString("") { "%02x".format(it) }
        }
    }
}
