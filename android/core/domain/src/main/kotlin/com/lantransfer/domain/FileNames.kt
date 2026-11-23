package com.lantransfer.domain

import java.time.LocalDateTime
import java.util.Locale

/** 接收端文件系统的命名约束（设计 C3.1）：长度上限 ANDROID / IOS 为 255 个 UTF‑8 字节，WINDOWS 为 255 个 UTF‑16 码元。 */
enum class FsProfile { ANDROID, WINDOWS, IOS }

internal object NameText {
    const val MAX_LENGTH = 255

    private val reserved: Set<String> =
        (listOf("CON", "PRN", "AUX", "NUL") + (1..9).map { "COM$it" } + (1..9).map { "LPT$it" }).toSet()

    /** 按 profile 计量的长度（前提：不含 Unpaired_Surrogate）。 */
    fun length(s: String, profile: FsProfile): Int = when (profile) {
        FsProfile.WINDOWS -> s.length
        FsProfile.ANDROID, FsProfile.IOS -> utf8Length(s)
    }

    private fun utf8Length(s: String): Int {
        var n = 0
        var i = 0
        while (i < s.length) {
            val c = s[i]
            n += when {
                c.code < 0x80 -> 1
                c.code < 0x800 -> 2
                c.isHighSurrogate() && i + 1 < s.length && s[i + 1].isLowSurrogate() -> { i++; 4 }
                else -> 3
            }
            i++
        }
        return n
    }

    fun fits(s: String, profile: FsProfile) = length(s, profile) <= MAX_LENGTH

    /** 扩展名 = 最后一个 "." 及其后的部分；"." 为首字符或不存在时为空。 */
    fun extensionStart(s: String): Int {
        val i = s.lastIndexOf('.')
        return if (i <= 0) s.length else i
    }

    fun dropLastCodePoint(s: String): String = if (s.isEmpty()) s else s.substring(0, s.offsetByCodePoints(s.length, -1))

    fun codePoints(s: String): Int = s.codePointCount(0, s.length)

    /** 第一个 "." 之前的部分去除末尾空格后，不区分大小写地等于 Windows 保留名。 */
    fun isReserved(s: String): Boolean {
        val dot = s.indexOf('.')
        val head = (if (dot < 0) s else s.substring(0, dot)).trimEnd(' ')
        // 保留名都是 ASCII；只做 ASCII 大小写折叠，保证两端结果一致
        return head.all { it.code < 0x80 } && head.uppercase(Locale.ROOT) in reserved
    }
}

/** 文件名处理（需求 16.4–16.10，设计 C3.1 的 S0–S2 与 L1–L4）。纯函数，C# 侧同名镜像。 */
object FileNameSanitizer {
    private const val ILLEGAL = "\\/:*?\"<>|"

    /** now = 接收方开始接收该文件时的本地时间，只在需要回退名时使用（16.9）。 */
    fun sanitize(raw: String, profile: FsProfile, now: LocalDateTime): String = trace(raw, profile, now).result

    /** 处理结果与实际改动了名称的步骤（"S0"–"S2"、"L1"–"L4"），供测试向量标注覆盖情况。 */
    data class Trace(val result: String, val steps: Set<String>)

    fun trace(raw: String, profile: FsProfile, now: LocalDateTime): Trace {
        val steps = LinkedHashSet<String>()
        fun note(step: String, before: String, after: String) = after.also { if (it != before) steps += step }
        // S0：每个 Unpaired_Surrogate 替换为 "_"
        val s0 = note("S0", raw, replaceUnpaired(raw))
        // S1：只保留最后一个 "/" 或 "\" 之后的部分；"." 或 ".." 视为空
        val cut = maxOf(s0.lastIndexOf('/'), s0.lastIndexOf('\\'))
        val s1 = note("S1", s0, s0.substring(cut + 1).let { if (it == "." || it == "..") "" else it })
        // S2：非法字符与控制字符替换为 "_"；记下扩展名 ext₀
        val s2 = note("S2", s1, buildString(s1.length) {
            for (c in s1) append(if (c in ILLEGAL || c.code <= 0x1F || c.code == 0x7F) '_' else c)
        })
        val ext0 = s2.substring(NameText.extensionStart(s2))
        // L：循环执行 L1–L4 直到一整轮没有变化
        var name = s2
        while (true) {
            val before = name
            if (profile == FsProfile.WINDOWS) name = note("L1", name, name.trimEnd(' ', '.'))
            if (name.isEmpty() || name == "." || name == "..") name = note("L2", name, fallback(now) + ext0)
            if (profile == FsProfile.WINDOWS && NameText.isReserved(name)) name = note("L3", name, "_$name")
            if (!NameText.fits(name, profile)) name = note("L4", name, shortenOnce(name))
            if (name == before) return Trace(name, steps)
        }
    }

    /** L4：删除主干末尾 1 个码点；主干只剩 1 个码点时，改为删除扩展名末尾 1 个码点。 */
    private fun shortenOnce(name: String): String {
        val e = NameText.extensionStart(name)
        val stem = name.substring(0, e)
        val ext = name.substring(e)
        return if (NameText.codePoints(stem) > 1) NameText.dropLastCodePoint(stem) + ext else stem + NameText.dropLastCodePoint(ext)
    }

    /** 回退名 file_YYYYMMDD_HHMMSS。 */
    fun fallback(now: LocalDateTime): String = String.format(
        Locale.ROOT, "file_%04d%02d%02d_%02d%02d%02d", now.year, now.monthValue, now.dayOfMonth, now.hour, now.minute, now.second,
    )

    private fun replaceUnpaired(s: String): String = buildString(s.length) {
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c.isHighSurrogate() && i + 1 < s.length && s[i + 1].isLowSurrogate()) {
                append(c).append(s[i + 1])
                i += 2
                continue
            }
            append(if (c.isSurrogate()) '_' else c)
            i++
        }
    }
}

/** 重名处理（需求 16.11）：返回 name 本身，或追加最小 " (n)" 后的名称。 */
object NameCollisionResolver {
    /** [exists] 由 ReceiveStorage 提供（WINDOWS 下不区分大小写）。 */
    fun resolve(name: String, profile: FsProfile, exists: (String) -> Boolean, startAt: Int = 1): String {
        require(startAt >= 1) { "startAt must be >= 1" }
        if (!exists(name)) return name
        val e = NameText.extensionStart(name)
        val stem = name.substring(0, e)
        val ext = name.substring(e)
        var n = startAt
        while (true) {
            val candidate = candidate(stem, ext, n, profile)
            if (!exists(candidate)) return candidate
            n++
        }
    }

    /** 主干′ + " (n)" + 扩展名：主干′ 是满足长度上限的最长主干前缀（至少 1 个码点），仍超限时按 16.8 截断扩展名。 */
    internal fun candidate(stem: String, ext: String, n: Int, profile: FsProfile): String {
        val suffix = " ($n)"
        var s = stem
        while (NameText.codePoints(s) > 1 && !NameText.fits(s + suffix + ext, profile)) s = NameText.dropLastCodePoint(s)
        var x = ext
        while (x.isNotEmpty() && !NameText.fits(s + suffix + x, profile)) x = NameText.dropLastCodePoint(x)
        return s + suffix + x
    }
}
