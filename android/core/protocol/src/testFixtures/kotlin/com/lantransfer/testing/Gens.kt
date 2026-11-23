package com.lantransfer.testing

import io.kotest.property.Arb
import io.kotest.property.arbitrary.arbitrary
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.flatMap
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.arbitrary.orNull

/**
 * 共享生成器（设计“属性测试约定 → 共享生成器”）。协议层的文本类生成器定义在这里；
 * 领域层的文件名、IPv4 与接口集合生成器以扩展函数形式定义在 core/domain 的 testFixtures 中，调用方式同为 `Gens.xxx()`。
 * C# 侧同名、分布相近的实现是 `LanTransfer.Testing.Gens`。
 */
object Gens {
    /** 合法的 Unicode 标量值（U+0000–U+10FFFF，不含 U+D800–U+DFFF），偏向 ASCII、CJK 与增补平面。 */
    fun scalarValue(): Arb<Int> = Arb.choose(
        4 to Arb.int(0x20..0x7E),
        2 to Arb.int(0x4E00..0x9FFF),
        2 to Arb.int(0x0000..0xD7FF),
        1 to Arb.int(0xE000..0xFFFF),
        2 to Arb.int(0x10000..0x10FFFF),
    )

    /** 需求 12.2–12.4 中某个字符类的一个代表片段。 */
    fun charClassFragment(): Arb<String> =
        Arb.element(TextCharClass.entries).flatMap { Arb.element(it.samples) }

    /** 文本片段：字符类片段、随机标量值串或普通文字。 */
    fun textFragment(): Arb<String> = Arb.choose(
        3 to charClassFragment(),
        3 to Arb.list(scalarValue(), 1..8).map { cps(*it.toIntArray()) },
        2 to Arb.element("a", "Hello", "你好，世界", "123", "LanTransfer", "\u00E9t\u00E9"),
    )

    /**
     * 不含 Unpaired_Surrogate 的 Unicode 字符串，偏向需求 12.2–12.4 的每个字符类；
     * 首尾以一定概率出现空白或 U+FEFF（覆盖“首尾空白”和“位于开头的 U+FEFF”）。
     */
    fun unicodeText(maxFragments: Int = 24): Arb<String> {
        val edge = Arb.element(TextCharClass.EDGE_WHITESPACE.samples + TextCharClass.BOM.samples).orNull(0.75)
        return Arb.bind(edge, Arb.list(textFragment(), 0..maxFragments), edge) { head, body, tail ->
            (head ?: "") + body.joinToString("") + (tail ?: "")
        }
    }

    /** 单独的高代理、单独的低代理，或次序颠倒的代理对（低在前、高在后）。 */
    fun loneSurrogate(): Arb<String> = Arb.choose(
        2 to Arb.int(0xD800..0xDBFF).map { it.toChar().toString() },
        2 to Arb.int(0xDC00..0xDFFF).map { it.toChar().toString() },
        1 to Arb.bind(Arb.int(0xDC00..0xDFFF), Arb.int(0xD800..0xDBFF)) { low, high -> "${low.toChar()}${high.toChar()}" },
    )

    /** 至少含 1 个 Unpaired_Surrogate 的 UTF-16 字符串（Property 17、24）。 */
    fun utf16WithUnpairedSurrogate(): Arb<String> = Arb.bind(
        Arb.list(textFragment(), 0..12),
        Arb.list(Arb.bind(loneSurrogate(), Arb.int(0..64)) { s, at -> s to at }, 1..3),
    ) { fragments, lones ->
        val pieces = fragments.toMutableList()
        lones.forEach { (s, at) -> pieces.add(at.coerceAtMost(pieces.size), s) }
        joinWithoutPairing(pieces)
    }

    /** 非法 UTF‑8 字节序列（Property 7、utf8-invalid.json）：合法前缀 + 1 段非法字节 + 合法后缀。 */
    fun invalidUtf8(): Arb<InvalidUtf8> = Arb.bind(unicodeText(6), invalidUtf8Core(), unicodeText(6)) { head, core, tail ->
        InvalidUtf8(head.encodeToByteArray() + core.bytes + tail.encodeToByteArray(), core.kind)
    }

    private fun invalidUtf8Core(): Arb<InvalidUtf8> = Arb.choice(
        // 截断的多字节序列：取 2–4 字节编码的前 1..n-1 个字节
        Arb.bind(Arb.int(0x80..0x10FFFF).map { if (it in 0xD800..0xDFFF) it + 0x800 else it }, Arb.int(1..3)) { cp, keep ->
            val full = cps(cp).encodeToByteArray()
            InvalidUtf8(full.copyOf(keep.coerceAtMost(full.size - 1)), InvalidUtf8.TRUNCATED)
        },
        // 非最短形式：C0/C1 开头的 2 字节、E0 80–9F 的 3 字节、F0 80–8F 的 4 字节
        arbitrary { rs ->
            val r = rs.random
            val bytes = when (r.nextInt(3)) {
                0 -> byteArrayOf((0xC0 + r.nextInt(2)).toByte(), cont(r.nextInt(0x40)))
                1 -> byteArrayOf(0xE0.toByte(), (0x80 + r.nextInt(0x20)).toByte(), cont(r.nextInt(0x40)))
                else -> byteArrayOf(0xF0.toByte(), (0x80 + r.nextInt(0x10)).toByte(), cont(r.nextInt(0x40)), cont(r.nextInt(0x40)))
            }
            InvalidUtf8(bytes, InvalidUtf8.OVERLONG)
        },
        // 编码 U+D800–U+DFFF 的 3 字节序列
        Arb.bind(Arb.int(0xA0..0xBF), Arb.int(0..0x3F)) { b1, b2 ->
            InvalidUtf8(byteArrayOf(0xED.toByte(), b1.toByte(), cont(b2)), InvalidUtf8.SURROGATE)
        },
        // 解码值大于 U+10FFFF：F4 90–BF xx xx，或 F5–F7 开头
        arbitrary { rs ->
            val r = rs.random
            val lead = if (r.nextBoolean()) 0xF4 else 0xF5 + r.nextInt(3)
            val second = if (lead == 0xF4) 0x90 + r.nextInt(0x30) else 0x80 + r.nextInt(0x40)
            InvalidUtf8(byteArrayOf(lead.toByte(), second.toByte(), cont(r.nextInt(0x40)), cont(r.nextInt(0x40))), InvalidUtf8.ABOVE_MAX)
        },
        // 任何位置都不合法的字节 F8–FF
        Arb.int(0xF8..0xFF).map { InvalidUtf8(byteArrayOf(it.toByte()), InvalidUtf8.INVALID_BYTE) },
        // 没有前导字节的续字节 80–BF
        Arb.list(Arb.int(0x80..0xBF), 1..3).map { list -> InvalidUtf8(ByteArray(list.size) { list[it].toByte() }, InvalidUtf8.UNEXPECTED_CONTINUATION) },
        // 前导字节后跟非续字节
        Arb.bind(Arb.element(0xC3, 0xE2, 0xF0), Arb.int(0x20..0x7E)) { lead, ascii ->
            InvalidUtf8(byteArrayOf(lead.toByte(), ascii.toByte()), InvalidUtf8.MISSING_CONTINUATION)
        },
    )

    private fun cont(low6: Int): Byte = (0x80 or (low6 and 0x3F)).toByte()

    /** 拼接片段；若前一段以高代理结尾、后一段以低代理开头，中间插入一个空格，避免意外拼成合法代理对。 */
    private fun joinWithoutPairing(pieces: List<String>): String = buildString {
        for (p in pieces) {
            if (isNotEmpty() && last().isHighSurrogate() && p.isNotEmpty() && p.first().isLowSurrogate()) append(' ')
            append(p)
        }
    }
}

/** 非法 UTF‑8 样例；[kind] 与 C# 侧 `InvalidUtf8.Kind` 的取值一致。 */
class InvalidUtf8(val bytes: ByteArray, val kind: String) {
    override fun toString(): String = "InvalidUtf8(kind=$kind, bytes=${bytes.joinToString("") { "%02x".format(it) }})"

    companion object {
        const val TRUNCATED = "truncated"
        const val OVERLONG = "overlong"
        const val SURROGATE = "surrogate"
        const val ABOVE_MAX = "above-max"
        const val INVALID_BYTE = "invalid-byte"
        const val UNEXPECTED_CONTINUATION = "unexpected-continuation"
        const val MISSING_CONTINUATION = "missing-continuation"
        val kinds = listOf(TRUNCATED, OVERLONG, SURROGATE, ABOVE_MAX, INVALID_BYTE, UNEXPECTED_CONTINUATION, MISSING_CONTINUATION)
    }
}

/** 字符串中是否含有 Unpaired_Surrogate。 */
fun String.hasUnpairedSurrogate(): Boolean {
    var i = 0
    while (i < length) {
        val c = this[i]
        if (c.isHighSurrogate()) {
            if (i + 1 < length && this[i + 1].isLowSurrogate()) { i += 2; continue }
            return true
        }
        if (c.isLowSurrogate()) return true
        i++
    }
    return false
}
