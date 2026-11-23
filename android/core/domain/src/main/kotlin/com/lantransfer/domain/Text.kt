package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.StrictUtf8

/** [TextRules.checkOutgoing] 的结果，检查顺序为 Empty → HasUnpairedSurrogate → TooLong → Ok（11.2、12.6、11.9）。 */
sealed interface OutgoingCheck {
    data object Empty : OutgoingCheck

    data object HasUnpairedSurrogate : OutgoingCheck

    data class TooLong(val utf8Bytes: Long) : OutgoingCheck

    data class Ok(val utf8: Bytes) : OutgoingCheck
}

/** [TextRules.collapse] 的结果：完整显示，或只显示 [0, prefixEnd) 并提供“展开”。 */
sealed interface Collapse {
    data object Full : Collapse

    data class Collapsed(val prefixEnd: Int) : Collapse
}

/** 文字规则（需求 11、12、21.6，设计 C3.3）。 */
object TextRules {
    const val COLLAPSE_LINES = 20
    const val COLLAPSE_CODE_POINTS = 1000
    const val PREVIEW_CODE_POINTS = 100

    fun checkOutgoing(text: String): OutgoingCheck {
        if (text.isEmpty()) return OutgoingCheck.Empty
        if (StrictUtf8.hasUnpairedSurrogate(text)) return OutgoingCheck.HasUnpairedSurrogate
        val n = StrictUtf8.encodedLength(text)
        if (n > ProtocolConstants.MAX_TEXT_BYTES) return OutgoingCheck.TooLong(n.toLong())
        return OutgoingCheck.Ok(Bytes.of(StrictUtf8.encode(text)))
    }

    /** 每个 Unpaired_Surrogate → U+FFFD，其余码元不变（12.10）。 */
    fun replaceUnpaired(text: String): String = buildString(text.length) {
        var i = 0
        while (i < text.length) {
            val c = text[i]
            if (c.isHighSurrogate() && i + 1 < text.length && text[i + 1].isLowSurrogate()) {
                append(c).append(text[i + 1])
                i += 2
                continue
            }
            append(if (c.isSurrogate()) '\uFFFD' else c)
            i++
        }
    }

    /** LF 数 + 其后不是 LF 的 CR 数 + 1（11.10）。 */
    fun lineCount(text: String): Int {
        var n = 1
        for (i in text.indices) {
            val c = text[i]
            if (c == '\n' || (c == '\r' && (i + 1 >= text.length || text[i + 1] != '\n'))) n++
        }
        return n
    }

    /**
     * 行数超过 20 或码点数超过 1000 时折叠；折叠前缀为同时满足“不超过 20 行、不超过 1000 个码点、
     * 结束于码点边界、不切开 CRLF”的最长前缀（设计 C3.3）。
     */
    fun collapse(text: String): Collapse {
        if (lineCount(text) <= COLLAPSE_LINES && text.codePointCount(0, text.length) <= COLLAPSE_CODE_POINTS) return Collapse.Full
        // 前缀的行数与码点数都随长度单调不减：找到最后一个满足两项上限的码点边界
        var best = 0
        var lines = 1
        var cps = 0
        var i = 0
        while (i < text.length) {
            val c = text[i]
            val width = if (c.isHighSurrogate() && i + 1 < text.length && text[i + 1].isLowSurrogate()) 2 else 1
            // 加入这个码点之后的前缀 [0, i + width)
            val newLines = lines + when {
                c == '\n' && i > 0 && text[i - 1] == '\r' -> 0 // CRLF 中的 LF：CR 已经计过一次
                c == '\n' || c == '\r' -> 1
                else -> 0
            }
            val newCps = cps + 1
            if (newLines > COLLAPSE_LINES || newCps > COLLAPSE_CODE_POINTS) break
            lines = newLines
            cps = newCps
            i += width
            best = i
        }
        // 不切开 CRLF：前缀以 CR 结尾而下一个是 LF 时，退到 CR 之前
        if (best > 0 && best < text.length && text[best - 1] == '\r' && text[best] == '\n') best--
        return Collapse.Collapsed(best)
    }

    /** 前 100 个码点，超出时以 "…" 结尾（21.6）。 */
    fun notificationPreview(text: String): String {
        if (text.codePointCount(0, text.length) <= PREVIEW_CODE_POINTS) return text
        return text.substring(0, text.offsetByCodePoints(0, PREVIEW_CODE_POINTS)) + "…"
    }
}

/**
 * 显示文本与“显示码元 → 原文”映射（设计 C3.3）。每个映射单元对应原文中的一段（CRLF、代理对或单个码元）
 * 与显示中的一段；[originalRange] 先把选区扩展到完整的映射单元，再返回对应的原文 UTF‑16 范围 [start, end)。
 */
class DisplayText internal constructor(
    val display: String,
    private val unitOfDisplay: IntArray,
    private val unitOrigStart: IntArray,
    private val unitOrigEnd: IntArray,
    private val originalLength: Int,
) {
    /** 显示选区 [selStart, selEnd) 对应的原文范围（end 不含）。 */
    fun originalRange(selStart: Int, selEnd: Int): IntRange {
        require(selStart in 0..selEnd && selEnd <= display.length) { "selection out of range" }
        if (selStart == selEnd) {
            val p = if (selStart == display.length) originalLength else unitOrigStart[unitOfDisplay[selStart]]
            return p until p
        }
        return unitOrigStart[unitOfDisplay[selStart]] until unitOrigEnd[unitOfDisplay[selEnd - 1]]
    }
}

/** 文字显示映射（需求 12.8、13.3、13.4）。 */
object TextDisplayMapper {
    private const val C1_PLACEHOLDER = '\u25AF' // ▯

    fun map(original: String): DisplayText {
        val display = StringBuilder(original.length)
        val unitOfDisplay = ArrayList<Int>(original.length)
        val starts = ArrayList<Int>()
        val ends = ArrayList<Int>()
        fun unit(origStart: Int, origEnd: Int, shown: String) {
            val u = starts.size
            starts += origStart
            ends += origEnd
            display.append(shown)
            repeat(shown.length) { unitOfDisplay += u }
        }
        var i = 0
        while (i < original.length) {
            val c = original[i]
            when {
                c == '\r' && i + 1 < original.length && original[i + 1] == '\n' -> { unit(i, i + 2, "\n"); i += 2 }
                c == '\r' || c == '\n' -> { unit(i, i + 1, "\n"); i++ }
                c == '\t' -> { unit(i, i + 1, "\t"); i++ }
                c.code <= 0x1F -> { unit(i, i + 1, (0x2400 + c.code).toChar().toString()); i++ }
                c.code == 0x7F -> { unit(i, i + 1, "\u2421"); i++ }
                c.code in 0x80..0x9F -> { unit(i, i + 1, C1_PLACEHOLDER.toString()); i++ }
                c.isHighSurrogate() && i + 1 < original.length && original[i + 1].isLowSurrogate() -> { unit(i, i + 2, original.substring(i, i + 2)); i += 2 }
                c.isSurrogate() -> { unit(i, i + 1, "\uFFFD"); i++ }
                else -> { unit(i, i + 1, c.toString()); i++ }
            }
        }
        return DisplayText(display.toString(), unitOfDisplay.toIntArray(), starts.toIntArray(), ends.toIntArray(), original.length)
    }
}
