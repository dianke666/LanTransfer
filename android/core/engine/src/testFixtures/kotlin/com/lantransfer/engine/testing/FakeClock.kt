@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine.testing

import com.lantransfer.engine.ports.Clock
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestCoroutineScheduler
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset

/**
 * 虚拟时钟。传入 [scheduler]（runTest 的 testScheduler）时，单调时间等于虚拟时间，引擎里的 delay / withTimeout
 * 与本时钟一致；不传时只靠 [advanceBy] 手动推进。另可用 [wallSkewMs] 模拟系统时间被修改（单调时间不受影响）。
 */
class FakeClock(
    private val scheduler: TestCoroutineScheduler? = null,
    private val wallStartMs: Long = DEFAULT_WALL_START_MS,
    private val offset: ZoneOffset = ZoneOffset.ofHours(8),
) : Clock {
    private var manualMs = 0L

    /** 加到 wallMs 上的偏移，模拟用户修改系统时间。 */
    var wallSkewMs: Long = 0

    /** 手动推进；有 scheduler 时同时推进虚拟时间并执行到期的协程。 */
    fun advanceBy(ms: Long) {
        require(ms >= 0) { "time only moves forward" }
        if (scheduler != null) {
            scheduler.advanceTimeBy(ms)
            scheduler.runCurrent()
        } else {
            manualMs += ms
        }
    }

    override fun monotonicMs(): Long = (scheduler?.currentTime ?: 0L) + manualMs

    override fun wallMs(): Long = wallStartMs + monotonicMs() + wallSkewMs

    override fun localNow(): LocalDateTime = LocalDateTime.ofInstant(Instant.ofEpochMilli(wallMs()), offset)

    companion object {
        /** 2025-10-09 16:53:20 UTC。 */
        const val DEFAULT_WALL_START_MS = 1_760_028_800_000L
    }
}
