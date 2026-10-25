package com.tinyfish.jeekmusic.ui.components

/** Keep generated LAN IDs out of ordinary library labels; full paths remain in file details. */
fun libraryPathLabel(path: String): String = path.replace(Regex("JeekMusic-([0-9a-f]{32})(?=/|$)")) {
    "JeekMusic-${it.groupValues[1].take(8)}…"
}
