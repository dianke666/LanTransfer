@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TransferTask
import com.lantransfer.engine.testing.FakeAppLifecycle
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryDatabase
import com.lantransfer.engine.testing.InMemoryHistoryStore
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.InMemoryOutgoingTextCache
import com.lantransfer.engine.testing.InMemoryReceiveStorage
import com.lantransfer.engine.testing.InMemorySettingsStore
import com.lantransfer.engine.testing.InMemorySourceFiles
import com.lantransfer.engine.testing.InMemoryTaskStore
import com.lantransfer.engine.testing.RecordingNotifier
import com.lantransfer.engine.testing.SeededRng
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.TestScope
import kotlin.random.Random

// 文件传输测试的公共装置。

/** 一台带 FileTransferEngine 的测试设备；[dispatch] 为 false 时不处理收到的应用消息。 */
class XferDevice(
    val d: TestDevice,
    val clock: FakeClock,
    scope: CoroutineScope,
    seed: Long,
    profile: FsProfile = FsProfile.ANDROID,
    timings: TransferTimings = TransferTimings(),
    dispatch: Boolean = true,
) {
    val db = InMemoryDatabase { clock.wallMs() }
    val store = InMemoryTaskStore(db)
    val history = InMemoryHistoryStore(db)
    val storage = InMemoryReceiveStorage(profile, clock)
    val sources = InMemorySourceFiles()
    val cache = InMemoryOutgoingTextCache(sources)
    val notifier = RecordingNotifier()
    val settings = InMemorySettingsStore()
    val lifecycle = FakeAppLifecycle()
    val engine = FileTransferEngine(
        d.manager, storage, sources, store, d.trust, notifier, settings, lifecycle, cache, clock, SeededRng(seed), scope, profile, timings = timings,
    )
    val notices = ArrayList<TransferNotice>()

    /** 调试：收到的每条非数据块消息。 */
    var trace: ((String) -> Unit)? = null

    init {
        if (dispatch) {
            dispatchTo(d.manager, scope) { s, m ->
                if (m !is com.lantransfer.protocol.FileChunk) trace?.invoke("${d.name} <- $m via ${System.identityHashCode(s)}")
                engine.onMessage(s, m)
            }
        }
    }

    fun view(id: TransferId): TransferTaskView? = engine.tasks.value.firstOrNull { it.id == id }

    fun task(id: TransferId): TransferTask = view(id)!!.task

    fun state(id: TransferId): TaskState? = view(id)?.task?.state

    /** 放入一个源文件，内容为种子化的随机字节。 */
    fun file(name: String, size: Int, seed: Long = name.hashCode().toLong()): SourceRef {
        val ref = SourceRef("src:${d.name}/$name")
        sources.put(ref, name, Bytes.of(Random(seed).nextBytes(size)))
        return ref
    }

    fun content(ref: SourceRef): Bytes = sources.entry(ref)!!.data

    /** 默认接收目录中已提交的文件。 */
    fun received(): Map<String, Bytes> = storage.filesIn(DirRef.Default)

    suspend fun awaitState(id: TransferId, s: TaskState) = engine.tasks.first { list -> list.any { it.id == id && it.task.state == s } }
}

/** A（发送方）与 B（接收方，监听）互相信任。 */
class XferWorld(scope: TestScope, timings: TransferTimings = TransferTimings(), bDispatch: Boolean = true) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = XferDevice(TestDevice("A", 1, iface("wlan0", 192, 168, 1, 2), net, clock, scope.backgroundScope), clock, scope.backgroundScope, 1, timings = timings)
    val b = XferDevice(
        TestDevice("B", 2, iface("wlan0", 192, 168, 1, 3), net, clock, scope.backgroundScope).also { it.listen() },
        clock, scope.backgroundScope, 2, timings = timings, dispatch = bDispatch,
    )

    init {
        a.d.trusts(b.d)
        b.d.trusts(a.d)
    }

    /** B 对 A 开启“自动接收文件”。 */
    fun autoAccept() {
        b.d.trust.setAutoAccept(a.d.id, true)
    }

    /** 断开 A、B 之间当前的连接（RST）。 */
    fun cut() = net.connections.forEach { it.reset() }
}

fun TestScope.moveTimeTo(t: Long) {
    val dt = t - testScheduler.currentTime
    if (dt > 0) testScheduler.advanceTimeBy(dt)
    testScheduler.runCurrent()
}

fun TestScope.moveTimeBy(dt: Long) = moveTimeTo(testScheduler.currentTime + dt)
