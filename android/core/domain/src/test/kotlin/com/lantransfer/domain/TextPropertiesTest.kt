package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.cps
import com.lantransfer.testing.hasUnpairedSurrogate
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll
import kotlin.random.Random

// ---- 参考模型 ----

/** 行数：按 CRLF、单独 CR、单独 LF 三种换行计数再加 1。 */
private fun refLineCount(t: String): Int = Regex("\r\n|\r|\n").findAll(t).count() + 1

/** 映射单元：CRLF 或单个码点（代理对或单个码元，Java 正则按码点匹配）。返回每个单元的原文范围 [start, end)。 */
private fun refUnits(t: String): List<Pair<Int, Int>> =
    Regex("\r\n|[\\s\\S]").findAll(t).map { it.range.first to it.range.last + 1 }.toList()

private fun isBreak(u: String): Boolean = u == "\r\n" || u == "\r" || u == "\n"

private fun refShown(u: String): String = when {
    isBreak(u) -> "\n"
    u == "\t" -> "\t"
    u.length == 1 && u[0].code < 0x20 -> (0x2400 + u[0].code).toChar().toString()
    u == "\u007F" -> "\u2421"
    u.length == 1 && u[0].code in 0x80..0x9F -> "\u25AF"
    u.length == 1 && u[0].isSurrogate() -> "\uFFFD"
    else -> u
}

/** 折叠前缀：在所有映射单元边界（即码点边界且不切开 CRLF）中，取行数 ≤ 20 且码点数 ≤ 1000 的最大者。 */
private fun refCollapse(t: String): Collapse {
    if (refLineCount(t) <= 20 && t.codePoints().count() <= 1000) return Collapse.Full
    var lines = 1
    var cps = 0
    val ok = mutableListOf(0)
    for ((a, b) in refUnits(t)) {
        val u = t.substring(a, b)
        if (isBreak(u)) lines++
        cps += u.codePointCount(0, u.length)
        if (lines <= 20 && cps <= 1000) ok += b
    }
    return Collapse.Collapsed(ok.max())
}

/** 位置 p 在码点边界上且不在 CRLF 中间。 */
private fun isUnitBoundary(t: String, p: Int): Boolean =
    p == 0 || p == t.length || !(t[p - 1].isHighSurrogate() && t[p].isLowSurrogate()) && !(t[p - 1] == '\r' && t[p] == '\n')

// ---- 生成器 ----

private val anyText: Arb<String> = Arb.choose(
    4 to Gens.unicodeText(),
    3 to Gens.utf16WithUnpairedSurrogate(),
    1 to Arb.element("", " ", "\u3000", " \t\n", "\r\n", "\r", "\u0000", "\uFEFF"),
)

private val lineBreak = Arb.element("\n", "\r\n", "\r", "\r\r\n", "\n\r", "\r\n\n")

/** 行数在 20 上下的文本。 */
private val manyLines: Arb<String> = Arb.bind(Arb.list(Gens.textFragment(), 15..26), Arb.list(lineBreak, 26..26)) { lines, seps ->
    buildString { lines.forEachIndexed { k, l -> append(l); if (k < lines.size - 1) append(seps[k]) } }
}

/** 码点数在 1000 上下、边界处跨着 CRLF / 代理对 / 组合字符的文本。 */
private val nearCodePointLimit: Arb<String> = Arb.bind(
    Arb.int(995..1001),
    Arb.element("\r\n", "😀", "\r", "\n", "\uD83D", "\uDE00", "e\u0301", "中"),
    Gens.unicodeText(3),
) { n, edge, rest -> "a".repeat(n) + edge + rest }

/** 码点数在 100 上下（通知预览边界）。 */
private val nearPreviewLimit: Arb<String> = Arb.bind(Arb.int(97..101), Arb.element("😀", "a", "\r\n", "\uDBFF", "")) { n, edge ->
    "中".repeat(n) + edge + "尾"
}

private val longFragments: Arb<String> = Arb.list(Gens.textFragment(), 150..320).map { it.joinToString("") }

private val textForLayout: Arb<String> = Arb.choose(
    4 to anyText,
    3 to manyLines,
    2 to nearCodePointLimit,
    2 to nearPreviewLimit,
    1 to longFragments,
)

/** UTF‑8 长度在 1 MiB 上下：前缀 ASCII + 多字节或单独代理项结尾。 */
private val nearByteLimit: Arb<String> = Arb.bind(Arb.int(-3..4), Arb.element("", "é", "中", "😀", "\uD800", "\uDC00")) { k, tail ->
    "a".repeat(ProtocolConstants.MAX_TEXT_BYTES - k) + tail
}

private val outgoingText: Arb<String> = Arb.choose(
    8 to anyText,
    1 to Arb.bind(Arb.element(" ", "\t", "\n", "\u3000", "\u00A0", "\r\n"), Arb.int(1..20)) { w, n -> w.repeat(n) },
    1 to nearByteLimit,
)

class TextPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 24: 发送前检查与代理项替换
    // Validates: Requirements 11.2, 11.3, 11.9, 12.6, 12.10, 13.7
    test("Property 24") {
        checkAll(Pbt.iterations(1_000), outgoingText) { t ->
            val expected = when {
                t.isEmpty() -> OutgoingCheck.Empty
                t.hasUnpairedSurrogate() -> OutgoingCheck.HasUnpairedSurrogate
                else -> {
                    val utf8 = t.encodeToByteArray()
                    if (utf8.size > ProtocolConstants.MAX_TEXT_BYTES) OutgoingCheck.TooLong(utf8.size.toLong()) else OutgoingCheck.Ok(Bytes.of(utf8))
                }
            }
            TextRules.checkOutgoing(t) shouldBe expected
            val replaced = TextRules.replaceUnpaired(t)
            val refReplaced = buildString { t.codePoints().forEach { appendCodePoint(if (it in 0xD800..0xDFFF) 0xFFFD else it) } }
            replaced shouldBe refReplaced
            replaced.hasUnpairedSurrogate() shouldBe false
            if (!t.hasUnpairedSurrogate()) replaced shouldBe t
        }
    }

    // Feature: lan-file-transfer, Property 25: 行数、折叠与通知预览
    // Validates: Requirements 11.10, 21.6
    test("Property 25") {
        checkAll(Pbt.iterations(1_000), textForLayout) { t ->
            TextRules.lineCount(t) shouldBe refLineCount(t)
            val c = TextRules.collapse(t)
            c shouldBe refCollapse(t)
            if (c is Collapse.Collapsed) {
                val prefix = t.substring(0, c.prefixEnd)
                (refLineCount(prefix) <= 20) shouldBe true
                (prefix.codePoints().count() <= 1000) shouldBe true
                isUnitBoundary(t, c.prefixEnd) shouldBe true
            }
            val cpArr = t.codePoints().toArray()
            val preview = if (cpArr.size <= 100) t else String(cpArr, 0, 100) + "…"
            TextRules.notificationPreview(t) shouldBe preview
        }
    }

    // Feature: lan-file-transfer, Property 26: 显示映射与选区还原原文
    // Validates: Requirements 12.8, 13.3, 13.4
    test("Property 26") {
        checkAll(Pbt.iterations(1_000), textForLayout) { t ->
            val units = refUnits(t)
            val shown = units.map { (a, b) -> refShown(t.substring(a, b)) }
            val d = TextDisplayMapper.map(t)
            d.display shouldBe shown.joinToString("")
            d.display.count { it == '\n' } shouldBe refLineCount(t) - 1
            // 每个显示码元所属的映射单元
            val unitAt = IntArray(d.display.length)
            var p = 0
            shown.forEachIndexed { k, s -> repeat(s.length) { unitAt[p++] = k } }
            fun expectedRange(a: Int, b: Int): Pair<Int, Int> = when {
                a == b && a == d.display.length -> t.length to t.length
                a == b -> units[unitAt[a]].first.let { it to it }
                else -> units[unitAt[a]].first to units[unitAt[b - 1]].second
            }
            fun check(a: Int, b: Int) {
                val r = d.originalRange(a, b)
                val got = r.first to r.last + 1
                got shouldBe expectedRange(a, b)
                isUnitBoundary(t, got.first) shouldBe true
                isUnitBoundary(t, got.second) shouldBe true
            }
            val n = d.display.length
            if (n <= 40) {
                for (a in 0..n) for (b in a..n) check(a, b)
            } else {
                val rnd = Random(t.hashCode())
                repeat(60) {
                    val a = rnd.nextInt(n + 1)
                    check(a, a + rnd.nextInt(n - a + 1))
                }
            }
            val all = d.originalRange(0, n)
            t.substring(all.first, all.last + 1) shouldBe t
        }
    }

    test("示例：发送前检查、行数与折叠") {
        TextRules.checkOutgoing("") shouldBe OutgoingCheck.Empty
        TextRules.checkOutgoing("  \n\t").shouldBeInstanceOf<OutgoingCheck.Ok>()
        TextRules.checkOutgoing("a\uD800") shouldBe OutgoingCheck.HasUnpairedSurrogate
        TextRules.checkOutgoing("a".repeat(ProtocolConstants.MAX_TEXT_BYTES)).shouldBeInstanceOf<OutgoingCheck.Ok>()
        TextRules.checkOutgoing("a".repeat(ProtocolConstants.MAX_TEXT_BYTES - 1) + "é") shouldBe OutgoingCheck.TooLong(1_048_577)
        TextRules.replaceUnpaired("x\uDC00\uD83D\uDE00\uD800") shouldBe "x\uFFFD\uD83D\uDE00\uFFFD"
        TextRules.lineCount("") shouldBe 1
        TextRules.lineCount("a\r\nb\rc\nd") shouldBe 4
        TextRules.lineCount("\r\r\n\n") shouldBe 4
        TextRules.collapse("l\n".repeat(19) + "l") shouldBe Collapse.Full
        TextRules.collapse("l\n".repeat(20) + "l") shouldBe Collapse.Collapsed(39)
        TextRules.collapse("a".repeat(999) + "\r\nb") shouldBe Collapse.Collapsed(999)
        TextRules.collapse("a".repeat(999) + "😀b") shouldBe Collapse.Collapsed(1001)
        TextRules.collapse("a".repeat(1000)) shouldBe Collapse.Full
        TextRules.notificationPreview("中".repeat(100)) shouldBe "中".repeat(100)
        TextRules.notificationPreview("中".repeat(99) + cps(0x1F600, 0x1F601)) shouldBe "中".repeat(99) + cps(0x1F600) + "…"
    }

    test("示例：显示映射与部分复制") {
        val d = TextDisplayMapper.map("a\r\nb\u0000\u007F\u0085\t \uD83D\uDE00\r")
        d.display shouldBe "a\nb\u2400\u2421\u25AF\t \uD83D\uDE00\n"
        d.originalRange(1, 2) shouldBe (1 until 3) // "\n" ↔ CRLF
        d.originalRange(9, 10) shouldBe (9 until 11) // 选中代理对的一半 → 整个代理对
        d.originalRange(0, d.display.length) shouldBe (0 until 12)
        TextDisplayMapper.map("x\uDC00").display shouldBe "x\uFFFD"
    }
})
