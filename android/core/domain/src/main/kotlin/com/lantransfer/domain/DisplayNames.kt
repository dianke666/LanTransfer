package com.lantransfer.domain

import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolConstants

/** [DisplayNameRules.validateUserInput] 的结果（需求 4.4、4.5、4.8）。 */
sealed interface NameCheck {
    /** [cleaned] 为清理后名称；[removedAny] 表示原输入中含有至少 1 个 Unpaired_Surrogate。 */
    data class Ok(val cleaned: String, val removedAny: Boolean) : NameCheck

    data object Empty : NameCheck

    data object TooLong : NameCheck

    data object HasControl : NameCheck
}

/** 设备显示名称规则（需求 4，设计 C3.2）。 */
object DisplayNameRules {
    const val DEFAULT_ANDROID_NAME = "Android 设备"
    const val DEFAULT_WINDOWS_NAME = "Windows 电脑"
    const val DEFAULT_IOS_NAME = "iOS 设备"

    /** Unicode White_Space 属性的码点（显式列出，不用 Character.isWhitespace）。 */
    fun isWhiteSpace(c: Char): Boolean = when (c.code) {
        in 0x0009..0x000D, 0x0020, 0x0085, 0x00A0, 0x1680, in 0x2000..0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 -> true
        else -> false
    }

    /** 删除全部 Unpaired_Surrogate，其余码元顺序不变（4.3、4.4）。 */
    fun removeUnpairedSurrogates(s: String): String = buildString(s.length) {
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c.isHighSurrogate() && i + 1 < s.length && s[i + 1].isLowSurrogate()) {
                append(c).append(s[i + 1])
                i += 2
                continue
            }
            if (!c.isSurrogate()) append(c)
            i++
        }
    }

    /** 只去除首尾属于 White_Space 表的码点（White_Space 全部在 BMP 内）。 */
    fun trimWhiteSpace(s: String): String {
        var start = 0
        var end = s.length
        while (start < end && isWhiteSpace(s[start])) start++
        while (end > start && isWhiteSpace(s[end - 1])) end--
        return s.substring(start, end)
    }

    /** 清理后名称 = trimWhiteSpace(removeUnpairedSurrogates(s))。 */
    fun cleaned(s: String): String = trimWhiteSpace(removeUnpairedSurrogates(s))

    private fun codePoints(s: String) = s.codePointCount(0, s.length)

    private fun isControl(c: Char) = c.code <= 0x1F || c.code in 0x7F..0x9F

    /** 型号或计算机名称 → 移除 → 去空白 → 取前 32 个码点；为空时用该设备类型的默认名称（4.3）。 */
    fun defaultName(modelOrComputerName: String?, type: DeviceType): String {
        val c = cleaned(modelOrComputerName.orEmpty())
        val n = minOf(codePoints(c), ProtocolConstants.MAX_NAME_CODE_POINTS)
        val head = c.substring(0, c.offsetByCodePoints(0, n))
        return head.ifEmpty {
            when (type) {
                DeviceType.ANDROID -> DEFAULT_ANDROID_NAME
                DeviceType.WINDOWS -> DEFAULT_WINDOWS_NAME
                DeviceType.IOS -> DEFAULT_IOS_NAME
            }
        }
    }

    /** 基于清理后名称判定：Ok(cleaned, removedAny) | Empty | TooLong | HasControl。 */
    fun validateUserInput(input: String): NameCheck {
        val c = cleaned(input)
        return when {
            c.isEmpty() -> NameCheck.Empty
            codePoints(c) > ProtocolConstants.MAX_NAME_CODE_POINTS -> NameCheck.TooLong
            c.any(::isControl) -> NameCheck.HasControl
            else -> NameCheck.Ok(c, removeUnpairedSurrogates(input).length != input.length)
        }
    }

    /** 从线上收到的名称只检查码点数 1–32（23.2）；控制字符原样显示。 */
    fun isAcceptableRemote(name: String): Boolean =
        codePoints(name) in ProtocolConstants.MIN_NAME_CODE_POINTS..ProtocolConstants.MAX_NAME_CODE_POINTS
}
