package com.lantransfer.domain

import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.hasUnpairedSurrogate
import com.lantransfer.testing.localDateTime
import com.lantransfer.testing.rawFileName
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.enum
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll

private const val ILLEGAL = "\\/:*?\"<>|"

/** Property 17 的输出约束。 */
private fun satisfiesConstraints(r: String, profile: FsProfile): Boolean =
    r.isNotEmpty() && r != "." && r != ".." && !r.hasUnpairedSurrogate() &&
        r.none { it in ILLEGAL || it.code <= 0x1F || it.code == 0x7F } && NameText.fits(r, profile) &&
        (profile != FsProfile.WINDOWS || (!r.endsWith(" ") && !r.endsWith(".") && !NameText.isReserved(r)))

private fun alnum(range: IntRange, chars: String = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"): Arb<String> =
    Arb.list(Arb.element(chars.toList()), range).map { it.joinToString("") }

/** 不含分隔符、非法字符与 Unpaired_Surrogate 的名称片段。 */
private val plainPart: Arb<String> = Arb.choose(
    3 to alnum(1..12),
    2 to Arb.element("报告", "照片", "😀", "é", "数据 2025", "a b", "x.y"),
)

class FileNamePropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 17: 文件名处理的输出约束与幂等
    // Validates: Requirements 16.4, 16.5, 16.6, 16.7, 16.8, 16.9, 16.10
    test("Property 17") {
        checkAll(Pbt.iterations(1_000), Gens.rawFileName(), Arb.enum<FsProfile>(), Gens.localDateTime(), Gens.localDateTime()) { raw, profile, now, now2 ->
            val r = FileNameSanitizer.sanitize(raw, profile, now)
            satisfiesConstraints(r, profile) shouldBe true
            FileNameSanitizer.sanitize(r, profile, now2) shouldBe r
        }
    }

    // Feature: lan-file-transfer, Property 18: 合法名称保留、去除路径、截断保留扩展名
    // Validates: Requirements 16.4, 16.5, 16.8
    test("Property 18") {
        val ext = alnum(1..31, "abcdefghijklmnopqrstuvwxyz0123456789").map { ".$it" }
        val longStem = Arb.bind(Arb.list(plainPart, 1..8), Arb.int(20..120)) { parts, n -> "x" + parts.joinToString("").repeat(n) }
        checkAll(
            Pbt.iterations(1_000), Gens.rawFileName(), Arb.enum<FsProfile>(), Gens.localDateTime(),
            Arb.bind(Gens.rawFileName(), plainPart) { p, x -> p to x }, Arb.bind(longStem, ext) { s, e -> s to e },
        ) { candidate, profile, now, (prefix, x), (stem, extension) ->
            // 已满足输出约束的名称原样保留
            if (satisfiesConstraints(candidate, profile)) FileNameSanitizer.sanitize(candidate, profile, now) shouldBe candidate
            // 去除路径：任意前缀加分隔符不影响结果
            val expected = FileNameSanitizer.sanitize(x, profile, now)
            FileNameSanitizer.sanitize("$prefix/$x", profile, now) shouldBe expected
            FileNameSanitizer.sanitize("$prefix\\$x", profile, now) shouldBe expected
            // 超长名称：扩展名不变，主干为原主干按码点的前缀
            val name = stem + extension
            if (!NameText.fits(name, profile)) {
                val r = FileNameSanitizer.sanitize(name, profile, now)
                val e = NameText.extensionStart(r)
                r.substring(e) shouldBe extension
                val resultStem = r.substring(0, e)
                stem.startsWith(resultStem) shouldBe true
                (resultStem.isEmpty() || !resultStem.last().isLowSurrogate() || resultStem.length < 2 || resultStem[resultStem.length - 2].isHighSurrogate()) shouldBe true
            }
        }
    }

    // Feature: lan-file-transfer, Property 19: 重名取最小序号
    // Validates: Requirements 16.11
    test("Property 19") {
        checkAll(
            Pbt.iterations(1_000), Gens.rawFileName(), Arb.enum<FsProfile>(), Gens.localDateTime(), Arb.int(0..6),
            Arb.list(Gens.rawFileName(), 0..4), Arb.int(0..3),
        ) { raw, profile, now, taken, others, nameMode ->
            val name = FileNameSanitizer.sanitize(raw, profile, now)
            val e = NameText.extensionStart(name)
            val stem = name.substring(0, e)
            val ext = name.substring(e)
            val existing = buildSet {
                if (nameMode != 0) add(name)
                for (k in 1..taken) add(NameCollisionResolver.candidate(stem, ext, k, profile))
                others.forEach { add(FileNameSanitizer.sanitize(it, profile, now)) }
            }
            fun key(s: String) = if (profile == FsProfile.WINDOWS) s.lowercase() else s
            val keys = existing.map(::key).toSet()
            val exists = { s: String -> key(s) in keys }
            val r = NameCollisionResolver.resolve(name, profile, exists)
            exists(r) shouldBe false
            NameText.fits(r, profile) shouldBe true
            if (!exists(name)) {
                r shouldBe name
            } else {
                val n = (1..taken + others.size + 1).first { NameCollisionResolver.candidate(stem, ext, it, profile) == r }
                (1 until n).forEach { k -> exists(NameCollisionResolver.candidate(stem, ext, k, profile)) shouldBe true }
            }
        }
    }
})
