package com.tinyfish.jeekmusic.util

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** MediaStore DATE_MODIFIED is Unix seconds, not milliseconds. No file IO during binding. */
object FileModified {
    fun isValid(seconds: Long): Boolean = seconds in 1..253402214399L // safe through local year 9999 in every zone

    fun format(seconds: Long, zone: ZoneId = ZoneId.systemDefault(), locale: Locale = Locale.getDefault()): String =
        if (isValid(seconds)) DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm", locale)
            .withZone(zone).format(Instant.ofEpochSecond(seconds)) else "—"
}
