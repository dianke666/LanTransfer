package com.lantransfer.protocol

/**
 * 严格 UTF‑8 编解码（规范 2.1、2.4）。不依赖平台字符集实现，保证两端与各 Android 版本行为一致。
 */
object StrictUtf8 {
    /** 编码为标准 UTF‑8。前置条件：[s] 不含 Unpaired_Surrogate，否则抛出 [IllegalArgumentException]。 */
    fun encode(s: String): ByteArray {
        val out = ByteArray(encodedLength(s))
        var p = 0
        var i = 0
        while (i < s.length) {
            val c = s[i]
            val cp: Int
            if (c.isHighSurrogate()) {
                cp = Character.toCodePoint(c, s[i + 1])
                i += 2
            } else {
                cp = c.code
                i += 1
            }
            when {
                cp < 0x80 -> out[p++] = cp.toByte()
                cp < 0x800 -> {
                    out[p++] = (0xC0 or (cp ushr 6)).toByte()
                    out[p++] = (0x80 or (cp and 0x3F)).toByte()
                }
                cp < 0x10000 -> {
                    out[p++] = (0xE0 or (cp ushr 12)).toByte()
                    out[p++] = (0x80 or ((cp ushr 6) and 0x3F)).toByte()
                    out[p++] = (0x80 or (cp and 0x3F)).toByte()
                }
                else -> {
                    out[p++] = (0xF0 or (cp ushr 18)).toByte()
                    out[p++] = (0x80 or ((cp ushr 12) and 0x3F)).toByte()
                    out[p++] = (0x80 or ((cp ushr 6) and 0x3F)).toByte()
                    out[p++] = (0x80 or (cp and 0x3F)).toByte()
                }
            }
        }
        return out
    }

    /** UTF‑8 编码长度；含 Unpaired_Surrogate 时抛出 [IllegalArgumentException]。 */
    fun encodedLength(s: String): Int {
        var n = 0
        var i = 0
        while (i < s.length) {
            val c = s[i]
            when {
                c.code < 0x80 -> { n += 1; i += 1 }
                c.code < 0x800 -> { n += 2; i += 1 }
                c.isHighSurrogate() -> {
                    require(i + 1 < s.length && s[i + 1].isLowSurrogate()) { "unpaired surrogate at index $i" }
                    n += 4
                    i += 2
                }
                c.isLowSurrogate() -> throw IllegalArgumentException("unpaired surrogate at index $i")
                else -> { n += 3; i += 1 }
            }
        }
        return n
    }

    /** 严格解码；不合法（截断、非最短形式、代理项、> U+10FFFF、多余续字节）时返回 null。 */
    fun decode(b: ByteArray): String? = decode(b, 0, b.size)

    fun decode(b: ByteArray, offset: Int, length: Int): String? {
        val sb = StringBuilder(length)
        var i = offset
        val end = offset + length
        while (i < end) {
            val b0 = b[i].toInt() and 0xFF
            if (b0 < 0x80) {
                sb.append(b0.toChar())
                i += 1
                continue
            }
            // Unicode 标准表 3-7：按首字节确定长度与第二字节的合法范围
            val need: Int
            val lo: Int
            val hi: Int
            when (b0) {
                in 0xC2..0xDF -> { need = 1; lo = 0x80; hi = 0xBF }
                0xE0 -> { need = 2; lo = 0xA0; hi = 0xBF }
                in 0xE1..0xEC, 0xEE, 0xEF -> { need = 2; lo = 0x80; hi = 0xBF }
                0xED -> { need = 2; lo = 0x80; hi = 0x9F }
                0xF0 -> { need = 3; lo = 0x90; hi = 0xBF }
                in 0xF1..0xF3 -> { need = 3; lo = 0x80; hi = 0xBF }
                0xF4 -> { need = 3; lo = 0x80; hi = 0x8F }
                else -> return null
            }
            if (end - i - 1 < need) return null // 截断
            val b1 = b[i + 1].toInt() and 0xFF
            if (b1 < lo || b1 > hi) return null
            var cp = (b0 and (0x3F ushr need)) shl 6 or (b1 and 0x3F)
            for (k in 2..need) {
                val bk = b[i + k].toInt() and 0xFF
                if ((bk and 0xC0) != 0x80) return null
                cp = (cp shl 6) or (bk and 0x3F)
            }
            sb.appendCodePoint(cp)
            i += need + 1
        }
        return sb.toString()
    }

    /** 字符串中是否含有 Unpaired_Surrogate。 */
    fun hasUnpairedSurrogate(s: String): Boolean {
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c.isHighSurrogate()) {
                if (i + 1 < s.length && s[i + 1].isLowSurrogate()) {
                    i += 2
                    continue
                }
                return true
            }
            if (c.isLowSurrogate()) return true
            i++
        }
        return false
    }
}
