package com.lantransfer.testing

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.ints.shouldBeInRange
import io.kotest.matchers.shouldBe
import io.kotest.property.RandomSource
import io.kotest.property.arbitrary.take

/** 领域层共享生成器的自检：覆盖设计“共享生成器”列出的各类文件名与地址情形。 */
class DomainGensTest : FunSpec({
    val rs = RandomSource.seeded(20261004L)

    test("rawFileName 覆盖空串、点名、路径、保留名、超长名、非法字符与 Unpaired_Surrogate") {
        val names = Gens.rawFileName().take(3_000, rs).toList()
        names.any { it.isEmpty() } shouldBe true
        names.any { it == "." || it == ".." } shouldBe true
        names.any { it.contains('/') } shouldBe true
        names.any { it.contains('\\') } shouldBe true
        names.any { it.substringBefore('.').trimEnd().equals("con", ignoreCase = true) && it != it.uppercase() } shouldBe true
        names.any { it.encodeToByteArray().size > 255 } shouldBe true
        names.any { it.endsWith(" ") || it.endsWith(".") } shouldBe true
        names.any { it.contains('\u0000') } shouldBe true
        names.any { it.hasUnpairedSurrogate() } shouldBe true
    }

    test("ipv4 覆盖三个私有地址段与公网地址") {
        val addresses = Gens.ipv4().take(3_000, rs).toList()
        addresses.any { (it ushr 24) == 10 } shouldBe true
        addresses.any { (it ushr 20) == (172 shl 4 or 1) } shouldBe true
        addresses.any { (it ushr 16) == (192 shl 8 or 168) } shouldBe true
        addresses.any { it == ipv4(8, 8, 8, 8) } shouldBe true
    }

    test("ipv4InSubnet 生成的地址在子网内") {
        val base = ipv4(192, 168, 1, 77)
        Gens.ipv4InSubnet(base, 24).take(500, rs).forEach { (it and subnetMask(24)) shouldBe (base and subnetMask(24)) }
        Gens.ipv4InSubnet(base, 32).take(50, rs).forEach { it shouldBe base }
    }

    test("interfaceSet 至多 4 个接口，前缀 8–32，并出现重叠子网") {
        val sets = Gens.interfaceSet().take(2_000, rs).toList()
        sets.forEach { set ->
            set.size shouldBeInRange 0..4
            set.forEach { it.prefixLength shouldBeInRange 8..32 }
        }
        sets.any { it.isEmpty() } shouldBe true
        sets.any { set ->
            set.any { a -> set.any { b -> a !== b && (a.address and subnetMask(b.prefixLength)) == (b.address and subnetMask(b.prefixLength)) } }
        } shouldBe true
    }

    test("toDottedQuad") {
        ipv4(192, 168, 0, 255).toDottedQuad() shouldBe "192.168.0.255"
        ipv4(255, 255, 255, 255).toDottedQuad() shouldBe "255.255.255.255"
    }
})
