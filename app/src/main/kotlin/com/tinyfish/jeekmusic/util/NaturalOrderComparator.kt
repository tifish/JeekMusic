package com.tinyfish.jeekmusic.util

/**
 * 自然排序："2.mp3" 排在 "10.mp3" 前面（需求 F-4）。数字段按数值比较，其余部分忽略大小写逐字比较。
 */
object NaturalOrderComparator : Comparator<String> {

    override fun compare(a: String, b: String): Int {
        var i = 0
        var j = 0
        while (i < a.length && j < b.length) {
            val ca = a[i]
            val cb = b[j]
            if (ca.isAsciiDigit() && cb.isAsciiDigit()) {
                val endA = digitRunEnd(a, i)
                val endB = digitRunEnd(b, j)
                val cmp = compareDigitRuns(a.substring(i, endA), b.substring(j, endB))
                if (cmp != 0) return cmp
                i = endA
                j = endB
            } else {
                val cmp = ca.lowercaseChar().compareTo(cb.lowercaseChar())
                if (cmp != 0) return cmp
                i++
                j++
            }
        }
        val remaining = (a.length - i).compareTo(b.length - j)
        return if (remaining != 0) remaining else a.compareTo(b)
    }

    private fun Char.isAsciiDigit() = this in '0'..'9'

    private fun digitRunEnd(s: String, start: Int): Int {
        var k = start
        while (k < s.length && s[k].isAsciiDigit()) k++
        return k
    }

    private fun compareDigitRuns(x: String, y: String): Int {
        val tx = x.trimStart('0')
        val ty = y.trimStart('0')
        if (tx.length != ty.length) return tx.length.compareTo(ty.length)
        val cmp = tx.compareTo(ty)
        // 数值相同时，前导零少的排前面（"2" < "02"），保证结果稳定
        return if (cmp != 0) cmp else x.length.compareTo(y.length)
    }
}
