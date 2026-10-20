package com.tinyfish.jeekmusic.data.scanner

import com.tinyfish.jeekmusic.SharedCases
import com.tinyfish.jeekmusic.cases
import com.tinyfish.jeekmusic.description
import org.junit.Assert.assertEquals
import org.junit.Test

/** 用例在 shared/testcases/file-name-inferrer.json（两端共用）。 */
class FileNameInferrerTest {

    @Test
    fun infer() {
        for (c in SharedCases.load("file-name-inferrer.json").cases()) {
            val expected = c.optJSONObject("expected")?.let { InferredName(it.getString("artist"), it.getString("title")) }
            assertEquals(c.description(), expected, FileNameInferrer.infer(c.getString("input"), FileNameFormat.valueOf(c.getString("format"))))
        }
    }
}
