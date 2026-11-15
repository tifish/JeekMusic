package com.tinyfish.jeekmusic.data.scanner

import android.content.Context
import android.os.FileObserver
import android.os.storage.StorageManager
import android.provider.MediaStore
import com.tinyfish.jeekmusic.data.sync.LanLocalDeferred
import dagger.hilt.android.qualifiers.ApplicationContext
import org.json.JSONObject
import javax.inject.Inject
import javax.inject.Singleton

/** Queries the system index, never opens audio or enumerates filesystem directories. */
@Singleton
class SyncMediaChanges @Inject constructor(@ApplicationContext private val context: Context, private val index: LocalFileIndex) {
    private val checked=mutableMapOf<String,Triple<String,Long,Long>>()
    @Synchronized fun check(mapping: JSONObject) {
        val volume=mapping.getString("volume")
        val directory=context.getSystemService(StorageManager::class.java).storageVolumes.singleOrNull { it.mediaStoreVolumeName==volume }?.directory
            ?: throw LanLocalDeferred("storage_unavailable")
        val prefix=mapping.getString("relative")
        val key=mapping.getString("key")+":"+mapping.optString("directoryIdentity")
        val version=MediaStore.getVersion(context,volume)
        // Changes concurrent with the query remain visible after this watermark.
        val generation=MediaStore.getGeneration(context,volume)
        val ticket=Triple(version,generation,index.refreshEpoch)
        if(checked[key]==ticket)return
        val old=index.mediaCheckpoint(key)
        if(old?.optString("version")==version && old.optLong("generation")==generation) { checked[key]=ticket;return }
        val reset=old==null || old.optString("version")!=version || old.optLong("generation")>generation
        val paths=JSONObject()
        val changed=linkedSetOf<String>()
        val projection=arrayOf(MediaStore.Audio.Media._ID,MediaStore.Audio.Media.RELATIVE_PATH,MediaStore.Audio.Media.DISPLAY_NAME,MediaStore.Audio.Media.GENERATION_MODIFIED)
        val selection=if(prefix.isEmpty())null else "${MediaStore.Audio.Media.RELATIVE_PATH} LIKE ? ESCAPE '\\'"
        val args=if(prefix.isEmpty())null else arrayOf(MediaStoreSource.escapeLike(prefix)+"%")
        val cursor=context.contentResolver.query(MediaStore.Audio.Media.getContentUri(volume),projection,selection,args,null)
            ?: throw LanLocalDeferred("media_index_unavailable")
        cursor.use { c ->
            while(c.moveToNext()) {
                val path=c.getString(1).orEmpty()+c.getString(2).orEmpty()
                val id=c.getLong(0).toString()
                paths.put(id,path)
                val previous=old?.optJSONObject("paths")?.optString(id)
                if(reset || previous!=path || c.getLong(3)>old!!.optLong("generation")) changed+=path
                if(!previous.isNullOrEmpty() && previous!=path) changed+=previous
            }
        }
        val previous=old?.optJSONObject("paths")
        previous?.keys()?.forEach { id -> if(!paths.has(id)) changed+=previous.getString(id) }
        // Hints are durable before the checkpoint advances: crash recovery may repeat
        // metadata observations but cannot lose a pending change.
        for(path in changed) index.changed(directory,path,FileObserver.CLOSE_WRITE)
        index.mediaCheckpoint(key,JSONObject().put("version",version).put("generation",generation).put("paths",paths))
        checked[key]=ticket
    }
}
