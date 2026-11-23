package com.lantransfer.testing

import io.kotest.common.ExperimentalKotest
import io.kotest.property.PropTestConfig

/**
 * 属性测试辅助（设计“属性测试约定”）。全部属性测试通过 [iterations] 取得配置：
 *
 * - 迭代次数 = base × `LT_PBT_SCALE`（未设置时为 1；夜间任务设为 10）；
 * - 设置 `LT_PBT_SEED` 后用该种子重放（Kotest 失败时会打印种子）。
 *
 * C# 侧的同名辅助是 `LanTransfer.Testing.Pbt`。
 */
object Pbt {
    const val SCALE_ENV = "LT_PBT_SCALE"
    const val SEED_ENV = "LT_PBT_SEED"

    /** 每个属性至少运行的次数。 */
    const val MIN_BASE_ITERATIONS = 100

    val scale: Int by lazy { parseScale(System.getenv(SCALE_ENV)) }
    val seed: Long? by lazy { parseSeed(System.getenv(SEED_ENV)) }

    /** 纯函数属性用 `iterations(1_000)`，引擎与存储属性用 `iterations(100)`。 */
    @OptIn(ExperimentalKotest::class) // PropTestConfig.iterations 在 Kotest 6 中标为实验性
    fun iterations(base: Int): PropTestConfig = PropTestConfig(iterations = count(base), seed = seed)

    /** 实际迭代次数：base × scale。 */
    fun count(base: Int): Int {
        require(base >= MIN_BASE_ITERATIONS) { "属性测试至少运行 $MIN_BASE_ITERATIONS 次，实际 base = $base" }
        return Math.multiplyExact(base, scale)
    }

    /** 未设置或为空时为 1；其余必须是正整数，否则报错，避免夜间任务悄悄按 1 倍运行。 */
    fun parseScale(raw: String?): Int {
        val text = raw?.trim().orEmpty()
        if (text.isEmpty()) return 1
        val value = text.toIntOrNull()
        require(value != null && value >= 1) { "$SCALE_ENV 必须是正整数，实际为 \"$raw\"" }
        return value
    }

    /** 未设置或为空时为 null（每次随机）；其余必须是 64 位整数。 */
    fun parseSeed(raw: String?): Long? {
        val text = raw?.trim().orEmpty()
        if (text.isEmpty()) return null
        return requireNotNull(text.toLongOrNull()) { "$SEED_ENV 必须是 64 位整数，实际为 \"$raw\"" }
    }
}
