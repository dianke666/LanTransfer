package com.lantransfer.engine

import com.lantransfer.engine.testing.FakeAppLifecycle
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryDatabase
import com.lantransfer.engine.testing.InMemoryHistoryStore
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.InMemoryOutgoingTextCache
import com.lantransfer.engine.testing.InMemorySettingsStore
import com.lantransfer.engine.testing.InMemorySourceFiles
import com.lantransfer.engine.testing.RecordingClipboard
import com.lantransfer.engine.testing.RecordingNotifier
import com.lantransfer.engine.testing.SeededRng
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.Text
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope

// 文字服务测试的公共装置。

/**
 * 测试用分发器（正式的分发由任务 19 的 EngineHost 完成）：把每个新注册 Session 的应用消息交给 [handle]。
 * 须在任何 Session 建立之前调用并 runCurrent，否则会错过 Opened 事件。
 */
fun dispatchTo(manager: SessionManager, scope: CoroutineScope, handle: (Session, ProtocolMessage) -> Unit) {
    scope.launch {
        manager.events.collect { e ->
            if (e is SessionEvent.Opened) scope.launch { for (m in e.session.incoming) handle(e.session, m) }
        }
    }
}

/** 一台带 TextService 的测试设备。[dispatch] 为 false 时不处理收到的应用消息（模拟对方不回应），测试直接读 Session.incoming。 */
class TextDevice(val d: TestDevice, clock: FakeClock, scope: CoroutineScope, seed: Long, dispatch: Boolean = true) {
    val db = InMemoryDatabase { clock.wallMs() }
    val history = InMemoryHistoryStore(db)
    val clipboard = RecordingClipboard()
    val notifier = RecordingNotifier()
    val settings = InMemorySettingsStore()
    val lifecycle = FakeAppLifecycle()
    val sources = InMemorySourceFiles()
    val cache = InMemoryOutgoingTextCache(sources)
    val service = TextService(d.manager, history, d.trust, clipboard, notifier, settings, lifecycle, cache, clock, SeededRng(seed), scope)
    val notices = ArrayList<TextNotice>()

    init {
        scope.launch { service.notices.collect { notices += it } }
        if (dispatch) dispatchTo(d.manager, scope) { s, m -> service.onMessage(s, m) }
    }

    fun items(peer: TestDevice): List<TextItem> = service.conversation(peer.id).value
}

/** A、B 两台互相信任的设备，B 在监听，A 知道 B 的地址。 */
class TextWorld(scope: TestScope, aDispatch: Boolean = true, bDispatch: Boolean = true) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = TextDevice(TestDevice("A", 1, iface("wlan0", 192, 168, 1, 2), net, clock, scope.backgroundScope), clock, scope.backgroundScope, 1, aDispatch)
    val b = TextDevice(
        TestDevice("B", 2, iface("wlan0", 192, 168, 1, 3), net, clock, scope.backgroundScope).also { it.listen() },
        clock, scope.backgroundScope, 2, bDispatch,
    )

    init {
        a.d.trusts(b.d)
        b.d.trusts(a.d)
    }

    /** A 到 B 的 Session（A 发起）。 */
    suspend fun openAB(): Session = (a.d.manager.open(Target.Device(b.d.id)) as OpenResult.Ok).session
}

fun messageIdOf(n: Int): MessageId = MessageId(Bytes16.of(ByteArray(16) { n.toByte() }))

/** 原始 TEXT：消息 ID 的 16 字节都是 [n]，正文为任意字节。 */
fun rawText(n: Int, body: ByteArray, sentAt: Long = 1_700_000_000_000): Text = Text(messageIdOf(n), sentAt, Bytes.of(body))
