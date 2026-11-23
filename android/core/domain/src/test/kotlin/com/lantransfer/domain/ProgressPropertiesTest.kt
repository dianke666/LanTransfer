package com.lantransfer.domain

import com.lantransfer.testing.Pbt
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.boolean
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll
import java.math.BigDecimal
import java.math.BigInteger
import java.math.RoundingMode
import java.util.Locale

private const val MAX_SIZE = 1L shl 59
private val sizeUnits = listOf("B", "KB", "MB", "GB", "TB")

/** 参考模型：BigDecimal 截断到 1 位小数，选使数值 ≥ 1 的最大单位。 */
private fun refFormat(bytes: Long): String {
    val b = BigInteger.valueOf(bytes)
    val k = (4 downTo 1).firstOrNull { BigInteger.valueOf(1024).pow(it) <= b } ?: return "$bytes B"
    val v = BigDecimal(b).divide(BigDecimal(BigInteger.valueOf(1024).pow(k)), 1, RoundingMode.FLOOR)
    return v.toPlainString() + " " + sizeUnits[k]
}

/** 文件大小：均匀分布、单位边界 ±2、1 位小数的进位边界 ±1、小数值与固定样例。 */
private val sizeBytes: Arb<Long> = Arb.choose(
    3 to Arb.long(0L..MAX_SIZE),
    3 to Arb.bind(Arb.int(0..5), Arb.long(-2L..2L)) { k, d -> ((1L shl minOf(10 * k, 59)) + d).coerceIn(0L, MAX_SIZE) },
    3 to Arb.bind(Arb.int(1..4), Arb.long(1L..1023L), Arb.int(0..9), Arb.long(-1L..1L)) { k, n, tenth, d ->
        val unit = 1L shl (10 * k)
        val m = n * 10 + tenth
        ((m * unit + 9) / 10 + d).coerceIn(0L, MAX_SIZE)
    },
    2 to Arb.long(0L..5000L),
    1 to Arb.element(0L, 1023L, 1024L, 1_048_575L, MAX_SIZE, MAX_SIZE - 1),
)

private sealed interface ProgressOp {
    val dt: Long

    data class Reset(override val dt: Long, val transferred: Long) : ProgressOp

    data class Sample(override val dt: Long, val delta: Long) : ProgressOp

    /** [totalMode]：0 → total = 0，1 → total = 当前值，2 → 当前值 + [extra]，3 → [extra]（可能小于当前值）。 */
    data class Snapshot(override val dt: Long, val totalMode: Int, val extra: Long, val completed: Boolean) : ProgressOp
}

private val dtArb: Arb<Long> = Arb.choose(
    2 to Arb.long(0L..500L),
    3 to Arb.long(500L..1500L),
    1 to Arb.long(2990L..3010L),
    1 to Arb.element(0L, 1L, 2999L, 3000L, 3001L),
    1 to Arb.long(3000L..10_000L),
)

private val opArb: Arb<ProgressOp> = Arb.choose(
    1 to Arb.bind(dtArb, Arb.choose(1 to Arb.element(0L), 2 to Arb.long(0L..(1L shl 40)))) { dt, v -> ProgressOp.Reset(dt, v) },
    6 to Arb.bind(dtArb, Arb.choose(1 to Arb.element(0L), 3 to Arb.long(0L..65_536L), 1 to Arb.long(0L..10_000_000L))) { dt, d -> ProgressOp.Sample(dt, d) },
    3 to Arb.bind(dtArb, Arb.int(0..3), Arb.long(0L..(1L shl 41)), Arb.boolean()) { dt, m, e, c -> ProgressOp.Snapshot(dt, m, e, c) },
)

private val progressCase: Arb<List<ProgressOp>> =
    Arb.bind(Arb.long(0L..1_000_000L), Arb.long(0L..(1L shl 40)), Arb.list(opArb, 1..60)) { t0, v0, ops -> listOf(ProgressOp.Reset(t0, v0)) + ops }

/** 参考实现：保留 reset 以来的全部样本，按定义直接计算。 */
private class RefProgress(private val windowMs: Long = 3_000) {
    private var segmentStart = 0L
    private val samples = mutableListOf<Pair<Long, Long>>()

    val current: Long get() = samples.lastOrNull()?.second ?: 0L

    fun reset(now: Long, v: Long) {
        segmentStart = now
        samples.clear()
        samples += now to v
    }

    fun sample(now: Long, v: Long) {
        if (samples.isEmpty()) reset(now, v) else samples += now to v
    }

    fun snapshot(now: Long, total: Long, completed: Boolean): Progress {
        val start = maxOf(now - windowMs, segmentStart)
        // 窗口起点时刻的已传输字节 = 起点之前（含）最后一个样本的值
        val base = samples.lastOrNull { it.first <= start }?.second ?: current
        val elapsed = now - start
        val rate = if (elapsed > 0) BigInteger.valueOf(current - base).times(BigInteger.valueOf(1000)).divide(BigInteger.valueOf(elapsed)).toLong() else 0L
        val remaining = maxOf(total - current, 0L)
        val eta = if (rate == 0L) null else BigInteger.valueOf(remaining).add(BigInteger.valueOf(rate - 1)).divide(BigInteger.valueOf(rate)).toLong()
        val percent = if (total == 0L) {
            if (completed) 100 else 0
        } else {
            BigInteger.valueOf(minOf(current, total)).times(BigInteger.valueOf(100)).divide(BigInteger.valueOf(total)).toInt()
        }
        return Progress(percent, current, total, rate, eta)
    }
}

class ProgressPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 27: 文件大小格式化
    // Validates: Requirements 15.1
    test("Property 27") {
        val locales = listOf(Locale.ROOT, Locale.GERMANY, Locale.forLanguageTag("ar-EG"), Locale.forLanguageTag("hi-IN-u-nu-deva"), Locale.forLanguageTag("fr-FR"))
        val original = Locale.getDefault()
        try {
            checkAll(Pbt.iterations(1_000), sizeBytes, Arb.element(locales)) { bytes, locale ->
                Locale.setDefault(locale)
                SizeFormatter.format(bytes) shouldBe refFormat(bytes)
            }
        } finally {
            Locale.setDefault(original)
        }
    }

    test("示例：SizeFormatter（Q6）") {
        SizeFormatter.format(0) shouldBe "0 B"
        SizeFormatter.format(1023) shouldBe "1023 B"
        SizeFormatter.format(1024) shouldBe "1.0 KB"
        SizeFormatter.format(1_048_575) shouldBe "1023.9 KB"
        SizeFormatter.format(1_048_576) shouldBe "1.0 MB"
        SizeFormatter.format(1536) shouldBe "1.5 KB"
        SizeFormatter.format(1L shl 40) shouldBe "1.0 TB"
        SizeFormatter.format(MAX_SIZE) shouldBe "524288.0 TB"
    }

    // Feature: lan-file-transfer, Property 28: 进度计算
    // Validates: Requirements 18.1
    test("Property 28") {
        checkAll(Pbt.iterations(1_000), progressCase) { ops ->
            val calc = ProgressCalculator()
            val ref = RefProgress()
            var now = 0L
            for (op in ops) {
                now += op.dt
                when (op) {
                    is ProgressOp.Reset -> {
                        calc.reset(now, op.transferred)
                        ref.reset(now, op.transferred)
                    }
                    is ProgressOp.Sample -> {
                        val v = ref.current + op.delta
                        calc.sample(now, v)
                        ref.sample(now, v)
                    }
                    is ProgressOp.Snapshot -> {
                        val total = when (op.totalMode) {
                            0 -> 0L
                            1 -> ref.current
                            2 -> ref.current + op.extra
                            else -> op.extra
                        }
                        val got = calc.snapshot(now, total, op.completed)
                        got shouldBe ref.snapshot(now, total, op.completed)
                        (got.percent in 0..100) shouldBe true
                        (got.etaSec == null) shouldBe (got.bytesPerSec == 0L)
                    }
                }
            }
        }
    }

    test("示例：进度、速率与剩余时间") {
        val c = ProgressCalculator()
        c.reset(0, 1000)
        c.snapshot(0, 10_000, false) shouldBe Progress(10, 1000, 10_000, 0, null)
        c.sample(1000, 2000)
        c.snapshot(1000, 10_000, false) shouldBe Progress(20, 2000, 10_000, 1000, 8)
        c.sample(4000, 3500)
        // 窗口 [1000, 4000]：增量 1500 B / 3 s
        c.snapshot(4000, 10_000, false) shouldBe Progress(35, 3500, 10_000, 500, 13)
        c.snapshot(8000, 10_000, false) shouldBe Progress(35, 3500, 10_000, 0, null)
        c.snapshot(8000, 0, false).percent shouldBe 0
        c.snapshot(8000, 0, true).percent shouldBe 100
    }
})
