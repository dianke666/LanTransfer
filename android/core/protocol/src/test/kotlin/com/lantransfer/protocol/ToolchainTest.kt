package com.lantransfer.protocol

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.int
import io.kotest.property.checkAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

/** 工具链检查：JUnit 5 测试能在 JUnit 平台上被发现并执行。 */
class ToolchainJUnitTest {
    @Test
    fun `junit jupiter test is discovered and run`() {
        assertEquals(4, 2 + 2)
    }
}

/** 工具链检查：Kotest 规格与 kotest-property 能在 JUnit 平台上被发现并执行。 */
class ToolchainKotestSpec : FunSpec({
    test("kotest property is discovered and run") {
        checkAll(Arb.int(), Arb.int()) { a, b -> (a + b) shouldBe (b + a) }
    }
})
