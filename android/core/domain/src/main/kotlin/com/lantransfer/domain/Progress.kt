package com.lantransfer.domain

import java.math.BigInteger

/** 文件大小格式化（需求 15.1，设计 C3.4，Q6）：1024 进制，B 以外截断到 1 位小数，与区域设置无关。 */
object SizeFormatter {
    private val units = listOf("B", "KB", "MB", "GB", "TB")

    /** 前置条件：0 ≤ bytes ≤ 2^59。 */
    fun format(bytes: Long): String {
        require(bytes in 0..(1L shl 59)) { "bytes out of range" }
        var k = 0
        while (k < units.size - 1 && bytes >= (1L shl (10 * (k + 1)))) k++
        if (k == 0) return "$bytes B"
        val tenths = bytes * 10 / (1L shl (10 * k))
        return "${tenths / 10}.${tenths % 10} ${units[k]}"
    }
}

/** 进度快照（需求 18.1）。[etaSec] 为 null 表示剩余时间未知（速率为 0）。 */
data class Progress(val percent: Int, val transferred: Long, val total: Long, val bytesPerSec: Long, val etaSec: Long?)

/**
 * 进度计算（设计 C3.4）：速率 = 最近 [windowMs] 内的字节增量 ÷ 窗口时长，reset 之后窗口相应缩短；
 * 时间由调用方传入（单调时钟毫秒）。
 */
class ProgressCalculator(private val windowMs: Long = 3_000) {
    private val times = ArrayDeque<Long>()
    private val values = ArrayDeque<Long>()
    private var resetAt = 0L

    /** 开始或续传时调用：已有字节不计入速率。 */
    fun reset(nowMono: Long, transferred: Long) {
        times.clear()
        values.clear()
        times.addLast(nowMono)
        values.addLast(transferred)
        resetAt = nowMono
    }

    fun sample(nowMono: Long, transferred: Long) {
        if (times.isEmpty()) {
            reset(nowMono, transferred)
            return
        }
        times.addLast(nowMono)
        values.addLast(transferred)
        // 只保留窗口起点之前的最后一个样本
        val start = maxOf(nowMono - windowMs, resetAt)
        while (times.size >= 2 && times[1] <= start) {
            times.removeFirst()
            values.removeFirst()
        }
    }

    fun snapshot(nowMono: Long, total: Long, completed: Boolean): Progress {
        val current = values.lastOrNull() ?: 0L
        val start = maxOf(nowMono - windowMs, resetAt)
        var base = values.firstOrNull() ?: current
        for (k in times.indices) if (times[k] <= start) base = values[k]
        val elapsed = nowMono - start
        val rate = if (elapsed > 0 && current > base) mulDiv(current - base, 1000, elapsed) else 0L
        val remaining = maxOf(total - current, 0L)
        // ⌈remaining ÷ rate⌉，写成不会溢出的形式
        val eta = if (rate == 0L) null else remaining / rate + if (remaining % rate == 0L) 0 else 1
        val percent = when {
            total <= 0L -> if (completed) 100 else 0
            else -> mulDiv(current.coerceIn(0L, total), 100, total).toInt()
        }
        return Progress(percent, current, total, rate, eta)
    }

    private companion object {
        /** ⌊a × b ÷ c⌋（a、b ≥ 0，c > 0）；乘积可能溢出时改用 BigInteger。 */
        fun mulDiv(a: Long, b: Long, c: Long): Long =
            if (a <= Long.MAX_VALUE / b) {
                a * b / c
            } else {
                BigInteger.valueOf(a).multiply(BigInteger.valueOf(b)).divide(BigInteger.valueOf(c)).toLong()
            }
    }
}
