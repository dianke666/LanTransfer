package com.lantransfer.domain

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.int
import io.kotest.property.checkAll

/** 工具链检查：本模块的 Kotest 属性测试能在 JUnit 平台上被发现并执行。 */
class ToolchainKotestSpec : FunSpec({
    test("kotest property is discovered and run") {
        checkAll(Arb.int(), Arb.int()) { a, b -> (a + b) shouldBe (b + a) }
    }
})
