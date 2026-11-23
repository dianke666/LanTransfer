package com.lantransfer.testing

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.string.shouldContain
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive

/** 测试向量接入检查：protocol/test-vectors 已作为测试资源可读，manifest 列出的文件都存在且结构正确。 */
class TestVectorsTest : FunSpec({
    test("manifest.json 可读且版本正确") {
        val manifest = TestVectors.manifest()
        manifest.getValue("format_version").jsonPrimitive.int shouldBe TestVectors.FORMAT_VERSION
        manifest.string("protocol_version") shouldBe "1.0"
    }

    test("manifest 列出的每个文件都存在并含 vectors 数组") {
        TestVectors.files().forEach { file ->
            val doc = TestVectors.load(file)
            doc.string("protocol_version") shouldBe "1.0"
            doc.array("vectors")
        }
    }

    test("README 随向量一起复制") {
        TestVectors.bytes("README.md").decodeToString() shouldContain "frames.json"
    }

    test("十六进制互转") {
        TestVectors.toHex(TestVectors.hex("00ff4c54")) shouldBe "00ff4c54"
        TestVectors.hex("").size shouldBe 0
    }
})
