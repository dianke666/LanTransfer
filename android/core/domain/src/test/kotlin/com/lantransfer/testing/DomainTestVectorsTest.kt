package com.lantransfer.testing

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe

/** 领域层测试同样能读取测试向量（filename-sanitizer.json 等由本模块的测试使用）。 */
class DomainTestVectorsTest : FunSpec({
    test("领域层测试类路径中有 test-vectors/manifest.json") {
        TestVectors.manifest().string("protocol_version") shouldBe "1.0"
    }
})
