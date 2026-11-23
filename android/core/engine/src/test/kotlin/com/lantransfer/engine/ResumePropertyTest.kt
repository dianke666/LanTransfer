@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.FileState
import com.lantransfer.domain.TaskState
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.TransferId
import com.lantransfer.testing.Pbt
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.random.Random

class ResumePropertyTest : FunSpec({
    test("Property 46: 续传等价") {
        // 任意文件集合（含 0 字节）；0–3 次中断（在任意字节位置断开连接，或接收方写入失败）；中断期间可能修改未完成的源文件、
        // 删除接收方的临时文件；由任意一方点击继续。最终每个文件与源文件当前内容逐字节一致。
        checkAll(Pbt.iterations(100), Arb.long()) { seed -> runTest { resumeScenario(seed) } }
    }

    test("Property 46 回归用例：接收方已完成而发送方未收到 FILE_RESULT；写入失败后由接收方继续") {
        for (seed in listOf(1L, -5214993440723400738L)) runTest { resumeScenario(seed) }
    }
})

private fun XferDevice.describe(id: TransferId): String =
    view(id)?.task?.let { t -> "${t.state} ${t.interrupt} ${t.files.map { "${it.state}/${it.persisted}/${it.size}" }}" } ?: "-"

/** Property 46 的一个用例（由 [seed] 决定）；[trace] 输出每一步，供调试。卡住时（虚拟时间 10 min）报告双方状态。 */
private suspend fun TestScope.resumeScenario(seed: Long, trace: (String) -> Unit = {}) {
    val rnd = Random(seed)
    val w = XferWorld(this)
    w.autoAccept()
    w.a.d.listen()
    w.b.d.knows(w.a.d)
    runCurrent()
    val refs = (0 until rnd.nextInt(1, 5)).map { i -> w.a.file("f$i", if (rnd.nextInt(4) == 0) 0 else rnd.nextInt(1, 400_000), rnd.nextLong()) }
    val log = ArrayList<String>()
    fun note(s: String) {
        log += s
        trace(s)
    }
    note("seed=$seed files=${refs.map { w.a.content(it).size }}")
    w.a.trace = trace
    w.b.trace = trace
    // 本轮的中断方式：在接收方写入到 trigger 字节时触发
    var trigger = Long.MAX_VALUE
    var failWrite = false
    var fired = false
    w.b.storage.writeFailure = { _, len ->
        if (!fired && len >= trigger) {
            fired = true
            if (failWrite) {
                "磁盘已满"
            } else {
                w.cut()
                null
            }
        } else {
            null
        }
    }
    fun arm() {
        fired = false
        failWrite = rnd.nextInt(3) == 0
        trigger = rnd.nextLong(0, 400_000)
    }
    /** 轮询（虚拟时间每次 10 ms）直到 [cond] 成立；虚拟时间 10 min 内不成立时报告双方状态。 */
    suspend fun until(id: TransferId, what: String, cond: () -> Boolean) {
        val deadline = testScheduler.currentTime + 600_000
        while (!cond()) {
            if (testScheduler.currentTime > deadline) error("stuck waiting for $what\nA=${w.a.describe(id)}\nB=${w.b.describe(id)}\n${log.joinToString("\n")}")
            delay(10)
        }
    }
    fun settled(s: TaskState?) = s != null && (s.isTerminal || s == TaskState.INTERRUPTED)
    val interruptions = rnd.nextInt(0, 4)
    if (interruptions > 0) arm()
    val id = w.a.engine.send(w.b.d.id, refs).shouldBeInstanceOf<SendOfferResult.Sent>().id
    for (round in 0..interruptions) {
        // 本轮结束：中断已触发且双方都已停下，或双方都已结束（续传之前的“已中断”状态不算）
        until(id, "round $round end") {
            val sa = w.a.state(id)
            val sb = w.b.state(id)
            (fired && settled(sa) && settled(sb)) || (sa?.isTerminal == true && sb?.isTerminal == true)
        }
        note("round=$round fired=$fired failWrite=$failWrite trigger=$trigger A=${w.a.describe(id)} B=${w.b.describe(id)}")
        if (w.a.state(id) != TaskState.INTERRUPTED) break
        runCurrent() // 让写盘协程处理完已收到的数据
        // 中断期间：修改两端都未完成（接收方也未在校验）的源文件、删除临时文件
        for ((k, r) in refs.withIndex()) {
            val rs = w.b.task(id).files[k].state
            if (w.a.task(id).files[k].state == FileState.COMPLETED || rs == FileState.COMPLETED || rs == FileState.VERIFYING) continue
            if (rnd.nextInt(5) == 0) {
                w.a.sources.modify(r, Bytes.of(rnd.nextBytes(rnd.nextInt(0, 300_000))), rnd.nextLong(1, 1_000_000))
                note("modify f$k")
            }
        }
        if (rnd.nextInt(4) == 0) {
            w.b.storage.tempRefs.forEach { w.b.storage.removeTemp(it) }
            note("remove temps")
        }
        if (round + 1 < interruptions) {
            arm()
        } else {
            trigger = Long.MAX_VALUE
            fired = false
        }
        // 接收方可能已经完成（最后的 FILE_RESULT 在断开时丢失）：此时只有发送方能点击继续
        val byReceiver = rnd.nextBoolean() && w.b.state(id) == TaskState.INTERRUPTED
        note("resume by ${if (byReceiver) "B" else "A"}")
        (if (byReceiver) w.b.engine.resume(id) else w.a.engine.resume(id)) shouldBe ResumeResult.Started
    }
    until(id, "completion") { w.a.state(id) == TaskState.COMPLETED && w.b.state(id) == TaskState.COMPLETED }
    withClue(log.joinToString("\n")) {
        val got = w.b.received()
        for ((k, r) in refs.withIndex()) {
            w.b.task(id).files[k].state shouldBe FileState.COMPLETED
            got["f$k"] shouldBe w.a.content(r)
        }
        w.b.storage.tempRefs.shouldBeEmpty()
    }
}
