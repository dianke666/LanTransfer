package com.lantransfer.engine

import app.cash.turbine.test
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.test.runTest

/** 工具链检查：coroutines-test 与 Turbine 在本模块的 Kotest 规格中可用。 */
class ToolchainKotestSpec : FunSpec({
    test("turbine collects a flow under runTest") {
        runTest {
            flowOf(1, 2).test {
                awaitItem() shouldBe 1
                awaitItem() shouldBe 2
                awaitComplete()
            }
        }
    }
})
