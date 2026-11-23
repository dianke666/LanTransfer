package com.lantransfer.testing

import io.kotest.assertions.throwables.shouldThrow
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainAll
import io.kotest.matchers.shouldBe
import io.kotest.property.RandomSource
import io.kotest.property.arbitrary.take
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction

/** 共享生成器与 Pbt 辅助的自检：生成结果满足各自的约定，且覆盖全部字符类与非法 UTF‑8 类别。 */
class GensTest : FunSpec({
    val rs = RandomSource.seeded(20261004L)

    test("unicodeText 不含 Unpaired_Surrogate，且覆盖需求 12.2–12.4 的每个字符类") {
        val samples = Gens.unicodeText().take(3_000, rs).toList()
        samples.forEach { it.hasUnpairedSurrogate() shouldBe false }
        val covered = TextCharClass.entries.filter { c -> samples.any { s -> c.samples.any { s.contains(it) } } }
        covered.map { it.label } shouldContainAll TextCharClass.labels
        samples.any { it.startsWith("\uFEFF") } shouldBe true
        samples.any { it.isNotEmpty() && it.last().isWhitespace() } shouldBe true
    }

    test("字符类标签唯一，代表片段都是合法的 Unicode 标量值") {
        TextCharClass.labels.toSet().size shouldBe TextCharClass.entries.size
        TextCharClass.entries.forEach { c -> c.samples.forEach { it.hasUnpairedSurrogate() shouldBe false } }
    }

    test("utf16WithUnpairedSurrogate 总含有 Unpaired_Surrogate") {
        Gens.utf16WithUnpairedSurrogate().take(2_000, rs).forEach { it.hasUnpairedSurrogate() shouldBe true }
    }

    test("invalidUtf8 被严格的 UTF-8 解码器拒绝，且覆盖全部类别") {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        val samples = Gens.invalidUtf8().take(2_000, rs).toList()
        samples.forEach { sample ->
            shouldThrow<CharacterCodingException> { decoder.reset().decode(ByteBuffer.wrap(sample.bytes)) }
        }
        samples.map { it.kind }.toSet() shouldBe InvalidUtf8.kinds.toSet()
    }

    test("hasUnpairedSurrogate 的判定") {
        "".hasUnpairedSurrogate() shouldBe false
        cps(0x1F600).hasUnpairedSurrogate() shouldBe false
        "\uD83D".hasUnpairedSurrogate() shouldBe true
        "\uDE00".hasUnpairedSurrogate() shouldBe true
        "\uDE00\uD83D".hasUnpairedSurrogate() shouldBe true
        "a\uD83Db".hasUnpairedSurrogate() shouldBe true
    }

    test("Pbt 解析 LT_PBT_SCALE 与 LT_PBT_SEED") {
        Pbt.parseScale(null) shouldBe 1
        Pbt.parseScale(" ") shouldBe 1
        Pbt.parseScale("10") shouldBe 10
        shouldThrow<IllegalArgumentException> { Pbt.parseScale("0") }
        shouldThrow<IllegalArgumentException> { Pbt.parseScale("x") }
        Pbt.parseSeed(null) shouldBe null
        Pbt.parseSeed("-42") shouldBe -42L
        shouldThrow<IllegalArgumentException> { Pbt.parseSeed("1.5") }
        shouldThrow<IllegalArgumentException> { Pbt.count(99) }
        Pbt.count(1_000) shouldBe 1_000 * Pbt.scale
        Pbt.iterations(100).seed shouldBe Pbt.seed
    }
})
