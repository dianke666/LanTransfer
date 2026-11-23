package com.lantransfer.domain

import com.lantransfer.testing.TestVectors
import com.lantransfer.testing.string
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainAll
import io.kotest.matchers.shouldBe
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.time.LocalDateTime

/** 文件名处理的示例测试与 filename-sanitizer.json 一致性测试（任务 8.3）。 */
class FileNamesTest : FunSpec({
    val now = LocalDateTime.of(2025, 1, 1, 9, 30, 5)
    fun a(s: String) = FileNameSanitizer.sanitize(s, FsProfile.ANDROID, now)
    fun w(s: String) = FileNameSanitizer.sanitize(s, FsProfile.WINDOWS, now)

    test("边界示例") {
        w("con.txt") shouldBe "_con.txt"
        a("con.txt") shouldBe "con.txt"
        w("Nul.tar.gz") shouldBe "_Nul.tar.gz"
        w("COM1") shouldBe "_COM1"
        w("CON .txt") shouldBe "_CON .txt"
        w("Com0.txt") shouldBe "Com0.txt"
        a("..") shouldBe "file_20250101_093005"
        w("a/../b") shouldBe "b"
        w("report.") shouldBe "report"
        a("report.") shouldBe "report."
        a("中".repeat(85)) shouldBe "中".repeat(85)
        a("中".repeat(86) + ".txt").let { it.endsWith(".txt") && it.encodeToByteArray().size <= 255 } shouldBe true
        w("file_20250101_093005.txt") shouldBe "file_20250101_093005.txt"
        w("a<b>c:d\"e|f?g*h.txt") shouldBe "a_b_c_d_e_f_g_h.txt"
        a("a\uD800b.txt") shouldBe "a_b.txt"
        w("...") shouldBe "file_20250101_093005"
    }

    test("重名取最小序号，超长时截断主干") {
        val taken = setOf("a.txt", "a (1).txt", "a (2).txt")
        NameCollisionResolver.resolve("a.txt", FsProfile.ANDROID, { it in taken }) shouldBe "a (3).txt"
        NameCollisionResolver.resolve("b.txt", FsProfile.ANDROID, { it in taken }) shouldBe "b.txt"
        NameCollisionResolver.resolve("a.txt", FsProfile.ANDROID, { it in taken }, startAt = 5) shouldBe "a (5).txt"
        val long = "x".repeat(251) + ".txt"
        val r = NameCollisionResolver.resolve(long, FsProfile.WINDOWS, { it == long })
        r shouldBe "x".repeat(247) + " (1).txt"
        r.length shouldBe 255
    }

    test("filename-sanitizer.json：两套期望输出逐条一致，并覆盖每个处理步骤与边界示例") {
        val vectors = TestVectors.vectors("filename-sanitizer.json")
        val steps = HashSet<String>()
        vectors.forEach { v ->
            val input = v["input"]?.jsonPrimitive?.content ?: v.string("input_utf16").chunked(4).map { it.toInt(16).toChar() }.joinToString("")
            val t = LocalDateTime.parse(v.string("now"))
            FileNameSanitizer.sanitize(input, FsProfile.ANDROID, t) shouldBe v.string("android")
            FileNameSanitizer.sanitize(input, FsProfile.WINDOWS, t) shouldBe v.string("windows")
            v.getValue("steps").jsonObject.values.forEach { arr -> arr.jsonArray.forEach { steps += it.jsonPrimitive.content } }
        }
        steps shouldContainAll listOf("S0", "S1", "S2", "L1", "L2", "L3", "L4")
        vectors.mapNotNull { it["input"]?.jsonPrimitive?.content } shouldContainAll listOf(
            "con.txt", "Nul.tar.gz", "COM1", "CON .txt", "..", "a/../b", "report.", "中".repeat(85), "file_20250101_093005.txt",
        )
    }
})
