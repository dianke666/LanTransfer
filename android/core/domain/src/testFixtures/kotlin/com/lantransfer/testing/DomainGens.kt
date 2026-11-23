package com.lantransfer.testing

import io.kotest.property.Arb
import io.kotest.property.arbitrary.arbitrary
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.constant
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import java.time.LocalDateTime
import java.time.ZoneOffset

// 领域层共享生成器：文件名、IPv4 与接口集合、本地时间。与协议层的 Gens 同属一个对象命名空间。

private val reservedStems: List<String> =
    listOf("CON", "PRN", "AUX", "NUL") + (1..9).map { "COM$it" } + (1..9).map { "LPT$it" }

private val extensions: List<String> =
    listOf("", ".txt", ".TXT", ".tar.gz", ".jpg", ".pdf", ".中文", ".", ". txt", ".a.b.c", ".longextension")

private val illegalChars: List<String> =
    listOf("<", ">", ":", "\"", "|", "?", "*", "\\", "/", "\u0000", "\u0001", "\u001F", "\u007F")

/**
 * 未经处理的文件名（Property 17、18）：路径成分、`.` / `..`、保留名的大小写与空格变体、多重扩展名、
 * 末尾空格和点、多字节超长名称、非法字符、空串，以及含 Unpaired_Surrogate 的名称。
 */
fun Gens.rawFileName(): Arb<String> {
    val stem = Arb.choose(
        3 to Arb.element("report", "照片", "a", "My File", "数据_2025", "😀表情", "x".repeat(10)),
        2 to unicodeText(4).map { it.ifEmpty { "f" } },
    )
    val plain = Arb.bind(stem, Arb.element(extensions)) { s, e -> s + e }
    // 保留名：随机大小写（con、Con、cOn……），后跟空格或点，再跟扩展名
    val reserved = arbitrary { rs ->
        val r = rs.random
        val cased = buildString {
            reservedStems[r.nextInt(reservedStems.size)].forEach { append(if (r.nextBoolean()) it.lowercaseChar() else it.uppercaseChar()) }
        }
        cased + listOf("", " ", "  ", ".")[r.nextInt(4)] + extensions[r.nextInt(extensions.size)]
    }
    val pathLike = Arb.bind(Arb.element("a/", "a\\", "../", "..\\", "/", "C:\\", "dir/sub/", "./", "a/../"), plain) { p, n -> p + n }
    val dots = Arb.element(".", "..", "...", " . ", ". .", " ", "   ")
    val trailing = Arb.bind(plain, Arb.element(" ", ".", "..", " .", ". ", "\u3000", "\t")) { n, t -> n + t }
    val longName = Arb.bind(Arb.element("中", "😀", "a", "é", "\u0915\u094D"), Arb.int(40..300), Arb.element(extensions)) { unit, n, e ->
        unit.repeat(n) + e
    }
    val illegal = Arb.bind(plain, Arb.list(Arb.element(illegalChars), 1..3), Arb.int(0..20)) { n, cs, at ->
        val i = at.coerceAtMost(n.length)
        n.substring(0, i) + cs.joinToString("") + n.substring(i)
    }
    val unpaired = Arb.bind(utf16WithUnpairedSurrogate(), Arb.element(extensions)) { s, e -> s + e }
    return Arb.choose(
        4 to plain,
        2 to reserved,
        2 to pathLike,
        1 to dots,
        2 to trailing,
        1 to longName,
        2 to illegal,
        1 to Arb.constant(""),
        1 to unpaired,
    )
}

/** 由 4 个八位组构成 IPv4 地址（大端，作为 Int 存放）。 */
fun ipv4(a: Int, b: Int, c: Int, d: Int): Int = (a shl 24) or (b shl 16) or (c shl 8) or d

/** IPv4 地址的点分十进制形式。 */
fun Int.toDottedQuad(): String = "${(this ushr 24) and 0xFF}.${(this ushr 16) and 0xFF}.${(this ushr 8) and 0xFF}.${this and 0xFF}"

/** IPv4 地址：各私有地址段、私有段的边界外侧、公网、回环、链路本地、组播、广播与 0.0.0.0。 */
fun Gens.ipv4(): Arb<Int> = Arb.choose(
    3 to Arb.bind(Arb.int(0..255), Arb.int(0..255), Arb.int(0..255)) { b, c, d -> ipv4(10, b, c, d) },
    3 to Arb.bind(Arb.int(16..31), Arb.int(0..255), Arb.int(0..255)) { b, c, d -> ipv4(172, b, c, d) },
    3 to Arb.bind(Arb.int(0..255), Arb.int(0..255)) { c, d -> ipv4(192, 168, c, d) },
    2 to Arb.int(),
    2 to Arb.element(
        ipv4(127, 0, 0, 1), ipv4(0, 0, 0, 0), ipv4(255, 255, 255, 255), ipv4(169, 254, 1, 1), ipv4(224, 0, 0, 1),
        ipv4(239, 255, 42, 58), ipv4(100, 64, 0, 1), ipv4(8, 8, 8, 8), ipv4(9, 255, 255, 255), ipv4(11, 0, 0, 0),
        ipv4(172, 15, 255, 255), ipv4(172, 32, 0, 0), ipv4(192, 167, 255, 255), ipv4(192, 169, 0, 0),
    ),
)

/** 前缀长度 8–32，偏向常见值。 */
fun Gens.prefixLength(): Arb<Int> = Arb.choose(
    3 to Arb.element(24, 16, 8, 30, 31, 32),
    2 to Arb.int(8..32),
)

/** 子网掩码（前缀长度 0–32）。 */
fun subnetMask(prefixLength: Int): Int = if (prefixLength == 0) 0 else (-1 shl (32 - prefixLength))

/** [address]/[prefixLength] 子网内的任意地址（含网络地址与广播地址）。 */
fun Gens.ipv4InSubnet(address: Int, prefixLength: Int): Arb<Int> = Arb.int().map { r ->
    val mask = subnetMask(prefixLength)
    (address and mask) or (r and mask.inv())
}

/** 生成器使用的本机接口描述；[kind] 取 WIFI / HOTSPOT / ETHERNET / OTHER，与领域层 IfaceKind 的名称一致。 */
data class InterfaceSpec(val name: String, val kind: String, val address: Int, val prefixLength: Int) {
    override fun toString(): String = "InterfaceSpec($name, $kind, ${address.toDottedQuad()}/$prefixLength)"

    companion object {
        val kinds = listOf("WIFI", "HOTSPOT", "ETHERNET", "OTHER")
    }
}

/** 本机接口集合（0–4 个）：私有或公网地址、/8–/32 前缀，并有一定概率与已生成的接口处于重叠的子网。 */
fun Gens.interfaceSet(): Arb<List<InterfaceSpec>> = arbitrary { rs ->
    val r = rs.random
    val count = r.nextInt(0, 5)
    val result = mutableListOf<InterfaceSpec>()
    repeat(count) { i ->
        val kind = InterfaceSpec.kinds[r.nextInt(InterfaceSpec.kinds.size)]
        val prefix = prefixLength().bind()
        val address = if (result.isNotEmpty() && r.nextInt(3) == 0) {
            val other = result[r.nextInt(result.size)]
            ipv4InSubnet(other.address, minOf(other.prefixLength, prefix)).bind()
        } else {
            ipv4().bind()
        }
        result += InterfaceSpec("if$i", kind, address, prefix)
    }
    result
}

/** 2000-01-01 至 2099-12-31 之间、精确到秒的本地时间（文件名回退名 file_YYYYMMDD_HHMMSS）。 */
fun Gens.localDateTime(): Arb<LocalDateTime> {
    val from = LocalDateTime.of(2000, 1, 1, 0, 0).toEpochSecond(ZoneOffset.UTC)
    val to = LocalDateTime.of(2099, 12, 31, 23, 59, 59).toEpochSecond(ZoneOffset.UTC)
    return Arb.long(from..to).map { LocalDateTime.ofEpochSecond(it, 0, ZoneOffset.UTC) }
}
