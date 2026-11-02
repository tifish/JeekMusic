package com.tinyfish.jeekmusic.data.preference

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.strings
import org.junit.Assert.*
import org.junit.Test

class DesktopLearningResultsTest {
    @Test fun alreadyValidatedPersonalResultsSurvivePublicRuleUpgradeOnly() {
        assertTrue(compatiblePersonalResults(6,9)); assertTrue(compatiblePersonalResults(6,10))
        assertFalse(compatiblePersonalResults(5,9)); assertFalse(compatiblePersonalResults(6,8)); assertFalse(compatiblePersonalResults(6,11))
    }
    @Test fun sharedFailureContractRetainsSafeFieldsAndStripsRemotePrivateText() {
        val contract=SharedCases.load("learning-failure-v1.json")
        assertEquals(contract.getJSONArray("codes").strings().toSet(),LearningFailure.resources.keys)
        val safe=LearningFailure.sanitize(contract.getJSONObject("safe"))!!
        assertEquals("decode_length",safe.getString("code")); assertEquals("decode",safe.getString("workerPhase"));assertEquals(480000,safe.getInt("expectedSamples"))
        val hostile=LearningFailure.sanitize(contract.getJSONObject("hostile"))!!
        val text=hostile.toString()+LearningFailure.diagnostics(hostile)
        for(secret in listOf("SECRET","private-token","secret-path","audio-name","private-path")) assertFalse(text.contains(secret))
        assertFalse(hostile.has("expectedSamples"));assertFalse(hostile.has("actualSamples"));assertFalse(hostile.has("exitCode"))
    }
}
