package com.lantransfer.testing

/**
 * 需求 12.2–12.4 列出的特殊字符类。[label] 与 `protocol/test-vectors/text-messages.json` 中的 `class` 标签一致，
 * 覆盖检查（任务 6.2 / 6.3）用 [labels] 比对“每个字符类至少 1 个向量”。C# 侧同名清单：`LanTransfer.Testing.TextCharClass`。
 *
 * [samples] 是该类的代表片段，全部由合法的 Unicode 标量值组成（不含 Unpaired_Surrogate）。
 */
enum class TextCharClass(val label: String, val samples: List<String>) {
    LF("lf", listOf("\n")),
    CR("cr", listOf("\r")),
    CRLF("crlf", listOf("\r\n")),
    TAB("tab", listOf("\t")),
    CONSECUTIVE_SPACES("consecutive-spaces", listOf("  ", "   ", "      ")),
    EDGE_WHITESPACE("edge-whitespace", listOf(" ", "\t", "\n", "\r\n", "\u00A0", "\u3000")),
    NBSP("nbsp", listOf("\u00A0")),
    IDEOGRAPHIC_SPACE("ideographic-space", listOf("\u3000")),
    NUL("nul", listOf("\u0000")),
    C0_CONTROL("c0-control", (0x01..0x1F).map { cps(it) }),
    DEL("del", listOf("\u007F")),
    C1_CONTROL("c1-control", (0x80..0x9F).map { cps(it) }),
    ZWJ_SEQUENCE(
        "zwj-sequence",
        listOf(
            cps(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467), // 家庭
            cps(0x1F3F3, 0xFE0F, 0x200D, 0x1F308), // 彩虹旗
            cps(0x1F469, 0x1F3FD, 0x200D, 0x1F4BB), // 带肤色的程序员
        ),
    ),
    SKIN_TONE_MODIFIER("skin-tone-modifier", listOf(cps(0x1F44D, 0x1F3FD), cps(0x1F44B, 0x1F3FF), cps(0x1F9D1, 0x1F3FB))),
    VARIATION_SELECTOR_16("variation-selector-16", listOf(cps(0x2764, 0xFE0F), cps(0x263A, 0xFE0F), cps(0x0031, 0xFE0F, 0x20E3))),
    ZERO_WIDTH("zero-width", listOf("\u200B", "\u200C", "\u200D", "\u2060")),
    BOM("bom", listOf("\uFEFF")),
    COMBINING_MARK("combining-mark", listOf("e\u0301", "a\u0308", "\u20DD", "\u0915\u094D\u0937", "\u1100\u1161\u11A8")),
    SUPPLEMENTARY_PLANE("supplementary-plane", listOf(cps(0x10000), cps(0x1F600), cps(0x20000), cps(0x2A6D6), cps(0x1D11E))),
    PRIVATE_USE("private-use", listOf(cps(0xE000), cps(0xF8FF), cps(0xF0000), cps(0xFFFFD), cps(0x100000), cps(0x10FFFD))),

    /** 截至 Unicode 16.0 仍未分配的码点（希腊文区空位、第 5 平面）。 */
    UNASSIGNED("unassigned", listOf(cps(0x0378), cps(0x0379), cps(0x50000), cps(0x8FFFD))),
    NONCHARACTER("noncharacter", listOf(cps(0xFFFE), cps(0xFFFF), cps(0xFDD0), cps(0xFDEF), cps(0x1FFFE), cps(0x10FFFF))),
    ;

    companion object {
        val labels: List<String> = entries.map { it.label }

        fun byLabel(label: String): TextCharClass? = entries.firstOrNull { it.label == label }
    }
}

/** 若干 Unicode 码点拼成的字符串（顶层函数：枚举项初始化时伴生对象尚未初始化，不能调用其中的函数）。 */
fun cps(vararg codePoints: Int): String = buildString { codePoints.forEach { appendCodePoint(it) } }
