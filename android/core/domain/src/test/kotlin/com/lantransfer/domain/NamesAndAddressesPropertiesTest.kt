package com.lantransfer.domain

import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.testing.Gens
import com.lantransfer.testing.InterfaceSpec
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.cps
import com.lantransfer.testing.hasUnpairedSurrogate
import com.lantransfer.testing.interfaceSet
import com.lantransfer.testing.ipv4
import com.lantransfer.testing.ipv4InSubnet
// 注：Gens.ipv4() 为生成器扩展函数，与同名的 ipv4(a, b, c, d) 构造函数同在该导入中
import com.lantransfer.testing.toDottedQuad
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.boolean
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.enum
import io.kotest.property.arbitrary.flatMap
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.arbitrary.orNull
import io.kotest.property.checkAll
import java.math.BigInteger

private val whiteSpaceTable: Set<Int> = ((0x09..0x0D) + listOf(0x20, 0x85, 0xA0, 0x1680) + (0x2000..0x200A) + listOf(0x2028, 0x2029, 0x202F, 0x205F, 0x3000)).toSet()

private fun refTrim(s: String): String = s.dropWhile { it.code in whiteSpaceTable }.dropLastWhile { it.code in whiteSpaceTable }

private fun refRemove(s: String): String {
    val keep = BooleanArray(s.length) { true }
    var i = 0
    while (i < s.length) {
        if (s[i].isHighSurrogate() && i + 1 < s.length && s[i + 1].isLowSurrogate()) { i += 2; continue }
        if (s[i].isSurrogate()) keep[i] = false
        i++
    }
    return s.filterIndexed { k, _ -> keep[k] }
}

/** 名称输入：偏向 Unpaired_Surrogate、首尾空白、控制字符和长度边界。 */
private val nameInput: Arb<String> = Arb.choose(
    3 to Gens.unicodeText(6),
    2 to Gens.utf16WithUnpairedSurrogate(),
    2 to Arb.bind(Arb.element(" ", "\u3000", "\u00A0", "\t", "\u2028", "\u001C", ""), Arb.int(0..40), Arb.element(" ", "\u205F", "\n", "")) { pre, n, post ->
        pre + "名".repeat(n) + post
    },
    1 to Arb.bind(Gens.unicodeText(3), Arb.element("\u0000", "\u001F", "\u007F", "\u0085", "\u009F")) { s, c -> "a$c$s" },
)

private fun refIpv4(s: String): Int? {
    val t = refTrim(s)
    if (!Regex("^(0|[1-9][0-9]{0,2})(\\.(0|[1-9][0-9]{0,2})){3}$").matches(t)) return null
    val parts = t.split('.').map { it.toInt() }
    if (parts.any { it > 255 }) return null
    return parts.fold(0) { acc, p -> (acc shl 8) or p }
}

private fun refPort(s: String): PortParse {
    val t = refTrim(s)
    if (t.isEmpty()) return PortParse.Default(ProtocolConstants.DEFAULT_PORT)
    if (!Regex("^[0-9]+$").matches(t)) return PortParse.Invalid
    val v = BigInteger(t)
    return if (v >= BigInteger.ONE && v <= BigInteger.valueOf(65535)) PortParse.Ok(v.toInt()) else PortParse.Invalid
}

private val addressText: Arb<String> = Arb.choice(
    Gens.ipv4().map { it.toDottedQuad() },
    Arb.bind(Gens.ipv4(), Arb.element(" ", "\u3000", "\t", "")) { a, w -> w + a.toDottedQuad() + w },
    Arb.element("1", "0x1.2.3.4", "1.2.3", "1.2.3.4.5", "256.1.1.1", "01.2.3.4", "1.2.3.04", "１.２.３.４", "١.٢.٣.٤", "1..2.3", "", " ", "a.b.c.d", "1.2.3.4 5", "-1.2.3.4"),
    Gens.unicodeText(3),
)

private val portText: Arb<String> = Arb.choice(
    Arb.int(0..70000).map { it.toString() },
    Arb.bind(Arb.int(1..65535), Arb.element("0", "00", "")) { p, z -> z + p },
    Arb.element("", " ", "\u3000", "１２３", "٤٢٥٨٠", "42580 ", " 42580", "4258O", "+1", "-1", "99999999999999999999", "0"),
)

private fun toIfaces(specs: List<InterfaceSpec>): List<LocalInterface> =
    specs.map { LocalInterface(it.name, IfaceKind.valueOf(it.kind), Ipv4(it.address), it.prefixLength) }

private val kindRank = listOf(IfaceKind.WIFI, IfaceKind.ETHERNET, IfaceKind.HOTSPOT, IfaceKind.OTHER)

private fun refClassify(t: Ipv4, ifaces: List<LocalInterface>): Route {
    if (ifaces.any { it.address == t }) return Route.Self
    if (!AddressRules.isPrivate(t)) return Route.NotLocal
    val inSubnet = ifaces.filter { (t.bits xor it.address.bits) and subnetMask(it.prefixLength) == 0 }
    if (inSubnet.isEmpty()) return Route.NotLocal
    val longest = inSubnet.maxOf { it.prefixLength }
    val best = inSubnet.filter { it.prefixLength == longest }.minWith(compareBy<LocalInterface> { kindRank.indexOf(it.kind) }.thenBy { it.name })
    return Route.Local(best)
}

/** 目标地址：偏向接口子网内的地址与接口本身的地址。 */
private val targetAndIfaces = Gens.interfaceSet().flatMap { specs ->
    val ifaces = toIfaces(specs)
    val pick = if (specs.isEmpty()) Gens.ipv4() else Arb.choice(Gens.ipv4(), Arb.element(specs).flatMap { Gens.ipv4InSubnet(it.address, it.prefixLength) }, Arb.element(specs.map { it.address }))
    Arb.bind(pick, Arb.element(listOf<LocalInterface?>(null) + ifaces)) { t, via -> Triple(Ipv4(t), via, ifaces) }
}

class NamesAndAddressesPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 20: 显示名称规则
    // Validates: Requirements 4.3, 4.4, 4.5, 4.8, 4.9
    test("Property 20") {
        checkAll(Pbt.iterations(1_000), nameInput, Arb.enum<DeviceType>()) { s, type ->
            DisplayNameRules.removeUnpairedSurrogates(s) shouldBe refRemove(s)
            DisplayNameRules.trimWhiteSpace(s) shouldBe refTrim(s)
            val c = refTrim(refRemove(s))
            val n = c.codePointCount(0, c.length)
            val expected = when {
                c.isEmpty() -> NameCheck.Empty
                n > 32 -> NameCheck.TooLong
                c.any { it.code <= 0x1F || it.code in 0x7F..0x9F } -> NameCheck.HasControl
                else -> NameCheck.Ok(c, s.hasUnpairedSurrogate())
            }
            val got = DisplayNameRules.validateUserInput(s)
            got shouldBe expected
            if (got is NameCheck.Ok) got.cleaned.hasUnpairedSurrogate() shouldBe false
            val d = DisplayNameRules.defaultName(s, type)
            val head = c.substring(0, c.offsetByCodePoints(0, minOf(n, 32)))
            val expectedDefault = when (type) {
                DeviceType.ANDROID -> "Android 设备"
                DeviceType.WINDOWS -> "Windows 电脑"
                DeviceType.IOS -> "iOS 设备"
            }
            d shouldBe head.ifEmpty { expectedDefault }
            d.hasUnpairedSurrogate() shouldBe false
        }
    }

    // Feature: lan-file-transfer, Property 21: IPv4 与端口解析
    // Validates: Requirements 6.3, 6.4
    test("Property 21") {
        checkAll(Pbt.iterations(1_000), addressText, portText, Gens.ipv4()) { a, p, ip ->
            AddressRules.parseIpv4(a)?.bits shouldBe refIpv4(a)
            AddressRules.parseIpv4(Ipv4(ip).toString()) shouldBe Ipv4(ip)
            AddressRules.parsePort(p) shouldBe refPort(p)
        }
    }

    // Feature: lan-file-transfer, Property 22: 本地地址判定
    // Validates: Requirements 2.1, 2.5, 2.9, 5.5, 6.9
    test("Property 22") {
        checkAll(Pbt.iterations(1_000), targetAndIfaces) { (t, via, ifaces) ->
            AddressRules.classify(t, ifaces) shouldBe refClassify(t, ifaces)
            val inSubnet = { i: LocalInterface -> (t.bits xor i.address.bits) and subnetMask(i.prefixLength) == 0 }
            val expected = AddressRules.isPrivate(t) && (if (via != null) inSubnet(via) else ifaces.any(inSubnet))
            AddressRules.acceptsInbound(t, via, ifaces) shouldBe expected
        }
    }

    // Feature: lan-file-transfer, Property 23: 二维码地址列表与候选地址顺序
    // Validates: Requirements 2.5, 8.5, 8.12, 23.8
    test("Property 23") {
        val qrCase = Gens.interfaceSet().flatMap { specs ->
            val ifaces = toIfaces(specs)
            val ip = if (specs.isEmpty()) Gens.ipv4() else Arb.choice(Gens.ipv4(), Arb.element(specs).flatMap { Gens.ipv4InSubnet(it.address, it.prefixLength) })
            Arb.bind(Arb.list(ip, 1..4), Arb.list(Arb.boolean(), 4..4), Arb.int(0..1).orNull()) { ips, outcomes, _ ->
                Triple(ifaces, ips.distinct().map(::Ipv4), outcomes)
            }
        }
        checkAll(Pbt.iterations(1_000), qrCase) { (ifaces, ips, outcomes) ->
            val list = AddressRules.qrAddressList(ifaces)
            list shouldBe ifaces.map { it.address }.distinct().take(4)
            val attempted = ArrayList<Ipv4>()
            val result = AddressRules.connectInQrOrder(ips, ifaces) { ip, _ ->
                attempted += ip
                if (outcomes[ips.indexOf(ip)]) "ok:$ip" else null
            }
            val local = ips.filter { refClassify(it, ifaces) is Route.Local }
            val firstOk = local.indexOfFirst { outcomes[ips.indexOf(it)] }
            if (firstOk < 0) {
                result shouldBe null
                attempted shouldBe local
            } else {
                result shouldBe "ok:${local[firstOk]}"
                attempted shouldBe local.take(firstOk + 1)
            }
        }
    }

    test("示例：默认名称、名称校验与地址解析") {
        DisplayNameRules.defaultName("  Pixel 9  ", DeviceType.ANDROID) shouldBe "Pixel 9"
        DisplayNameRules.defaultName("\u3000", DeviceType.WINDOWS) shouldBe "Windows 电脑"
        DisplayNameRules.defaultName(null, DeviceType.ANDROID) shouldBe "Android 设备"
        DisplayNameRules.defaultName(null, DeviceType.IOS) shouldBe "iOS 设备"
        DisplayNameRules.defaultName(cps(*IntArray(40) { 0x1F600 }), DeviceType.ANDROID) shouldBe cps(*IntArray(32) { 0x1F600 })
        DisplayNameRules.validateUserInput(" a\uD800b ") shouldBe NameCheck.Ok("ab", true)
        DisplayNameRules.validateUserInput("a\u0085b") shouldBe NameCheck.HasControl
        DisplayNameRules.validateUserInput("\u0085") shouldBe NameCheck.Empty
        DisplayNameRules.validateUserInput("名".repeat(33)) shouldBe NameCheck.TooLong
        AddressRules.parseIpv4(" 192.168.1.20 ") shouldBe Ipv4.of(192, 168, 1, 20)
        AddressRules.parseIpv4("192.168.01.20") shouldBe null
        AddressRules.parsePort("") shouldBe PortParse.Default(42580)
        AddressRules.parsePort("00080") shouldBe PortParse.Ok(80)
        AddressRules.parsePort("65536") shouldBe PortParse.Invalid
        AddressRules.isPrivate(Ipv4.of(169, 254, 3, 4)) shouldBe true
        AddressRules.isPrivate(Ipv4.of(100, 64, 0, 1)) shouldBe false
        val wifi = LocalInterface("wlan0", IfaceKind.WIFI, Ipv4.of(192, 168, 1, 5), 24)
        val hotspot = LocalInterface("ap0", IfaceKind.HOTSPOT, Ipv4.of(192, 168, 1, 1), 24)
        AddressRules.classify(Ipv4.of(192, 168, 1, 9), listOf(hotspot, wifi)) shouldBe Route.Local(wifi)
        AddressRules.classify(Ipv4.of(192, 168, 1, 5), listOf(wifi)) shouldBe Route.Self
        AddressRules.classify(Ipv4.of(8, 8, 8, 8), listOf(wifi)) shouldBe Route.NotLocal
        wifi.broadcast shouldBe Ipv4.of(192, 168, 1, 255)
    }
})
