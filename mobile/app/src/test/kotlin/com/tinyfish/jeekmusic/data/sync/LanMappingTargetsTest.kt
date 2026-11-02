package com.tinyfish.jeekmusic.data.sync

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class LanMappingTargetsTest {
    private fun mapping(root: String,relative: String,volume: String="external_primary")=JSONObject().put("layoutVersion",2).put("rootId",root).put("volume",volume).put("relative",relative)
    @Test fun directSiblingTargetsAreAllowedWithoutAnAddedNamespace() {
        val current=listOf(mapping("a","Music/A/"))
        LanMappingTargets.requireSeparate(current,"b","external_primary","Music/B/")
        LanMappingTargets.requireSeparate(current,"b","sd_card","Music/A/")
        LanMappingTargets.requireSeparate(current,"a","external_primary","Music/A/")
    }
    @Test fun sameEnclosingNestedAndCaseVariantTargetsAreRejected() {
        val current=listOf(mapping("a","Music/A/"))
        for(path in listOf("Music/A/","Music/","Music/A/Child/","MUSIC/a/")) assertThrows(IllegalArgumentException::class.java) { LanMappingTargets.requireSeparate(current,"b","external_primary",path) }
    }
    @Test fun obsoleteMappingsAreNeverExecutableOrReusedAsNewTargetOwnership() {
        val old=mapping("a","Music/JeekMusic-a/").apply { remove("layoutVersion") }
        assertFalse(LanMappingTargets.isCurrent(old));assertThrows(IllegalArgumentException::class.java) { LanMappingTargets.requireCurrent(old) }
        LanMappingTargets.requireSeparate(listOf(old),"b","external_primary","Music/")
        assertEquals("Music/JeekMusic-a/",old.getString("relative"))
    }
}
