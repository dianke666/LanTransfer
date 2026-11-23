package com.lantransfer.engine

import com.lantransfer.domain.Direction
import com.lantransfer.domain.OutgoingCheck
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.Rng
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TextEffect
import com.lantransfer.domain.TextEvent
import com.lantransfer.domain.TextKey
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextReducer
import com.lantransfer.domain.TextRules
import com.lantransfer.domain.TextState
import com.lantransfer.domain.TextStatus
import com.lantransfer.engine.ports.AppLifecycle
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.engine.ports.Clipboard
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.HistoryStore
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.Notifier
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.OutgoingTextCache
import com.lantransfer.engine.ports.SettingsStore
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.StrictUtf8
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TextCorrupt
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.Locale

// 文字服务（设计 C4.4、C1.12，需求 9.2、11、12.6–12.12、13.6、13.8、20.8、21.6、25.1）：执行 TextReducer 的 effects，
// 按 C1.12 的顺序处理收到的文字。全部状态只在一个 actor 协程里修改；打开 Session、计时与写剪贴板在 actor 之外进行，
// 结果作为命令送回 actor。收到的消息由分发器（EngineHost）经 [TextService.onMessage] 交进来。

/** 会话界面中的一条文字。 */
data class TextItem(
    val key: TextKey,
    val direction: Direction,
    /** 完整原文（复制时写入剪贴板的就是它，13.2）。 */
    val text: String,
    val sentAtUtcMs: Long,
    val recordedAt: RecordTime,
    /** 发出的：SENDING / DELIVERED / FAILED；收到的：RECEIVED。 */
    val status: TextStatus,
) {
    /** 发送失败时显示“重试”（11.6、12.12）。 */
    val showRetry: Boolean get() = direction == Direction.SEND && status == TextStatus.FAILED
}

/** [TextService.send] 的结果（发送前检查，C1.12）。 */
sealed interface SendCheck {
    /** 已以“发送中”显示在会话末尾（11.11）。 */
    data class Sent(val id: MessageId) : SendCheck

    /** 文字为空（界面已禁用“发送”，11.2；这里兜底，不发送）。 */
    data object Empty : SendCheck

    /** 含 Unpaired_Surrogate：未发送；界面弹出“替换并发送 / 取消”（12.6），选择替换时用 TextRules.replaceUnpaired 的结果再次调用 send（12.10）。 */
    data object HasUnpairedSurrogate : SendCheck

    /** UTF‑8 编码超过 1 MiB：未发送；界面提示并提供“以 .txt 文件发送”（11.9，见 [TextService.writeTextFile]）。 */
    data class TooLong(val utf8Bytes: Long) : SendCheck
}

/** 需要界面提示的事件。 */
sealed interface TextNotice {
    /** History_Store 读取或写入失败（20.8）；文字收发照常进行。 */
    data class StoreFailed(val detail: String) : TextNotice

    /**
     * 发送文字时无法建立 Session。消息本身在点击后满 10 s 时标为“发送失败”（11.6）；KeyMismatch、NotTrusted 等
     * 需要界面按 9.4、9.5 另行提示。
     */
    data class ConnectFailed(val peer: DeviceId, val failure: OpenFailure) : TextNotice

    /** 自动复制写入剪贴板失败（13.9）。 */
    data object AutoCopyFailed : TextNotice
}

class TextService(
    private val sessions: SessionManager,
    private val history: HistoryStore,
    private val trust: TrustStore,
    private val clipboard: Clipboard,
    private val notifier: Notifier,
    private val settings: SettingsStore,
    private val lifecycle: AppLifecycle,
    private val textCache: OutgoingTextCache,
    private val clock: Clock,
    private val rng: Rng,
    private val scope: CoroutineScope,
) {
    private val inbox = Channel<Cmd>(Channel.UNLIMITED)
    private val noticeFlow = MutableSharedFlow<TextNotice>(extraBufferCapacity = 64)

    /** 每个对端的会话条目；访问映射本身要 synchronized，条目列表只在 actor 中修改。 */
    private val conversations = HashMap<DeviceId, MutableStateFlow<List<TextItem>>>()

    /** 自动复制的原文：按收到的顺序串行写入剪贴板，最后收到的一条留在剪贴板中（13.8）。 */
    private val copies = Channel<String>(Channel.UNLIMITED)

    // ---- 只在 actor 中访问 ----

    private var state = TextState()

    /**
     * 本次运行中已显示过的收到的文字 → 是否已写入 History_Store（C1.12 的“已显示集合”）。写库失败时仍然显示，
     * 发送方因收不到 ACK 而重试时不再重复显示（20.8）；已保存的消息此后只回复 TEXT_ACK，即使记录已被删除也不重新保存。
     */
    private val shown = HashMap<TextKey, Boolean>()

    /** 每个对端一个发送队列，保证同一对端的文字按点击顺序入队。 */
    private val outboxes = HashMap<DeviceId, Channel<Outgoing>>()

    private sealed interface Cmd {
        class Send(val peer: DeviceId, val utf8: Bytes, val text: String, val now: Long, val done: CompletableDeferred<MessageId>) : Cmd

        class Retry(val id: MessageId, val now: Long) : Cmd

        class Inbound(val session: Session, val msg: ProtocolMessage) : Cmd

        data object Tick : Cmd

        class Restore(val done: CompletableDeferred<Unit>) : Cmd

        /** [keys] 为 null 表示全部。 */
        class Forget(val keys: Set<TextKey>?, val done: CompletableDeferred<Unit>) : Cmd
    }

    /** 待发出的 TEXT 与它的 10 s 截止时刻（单调时钟）。 */
    private class Outgoing(val msg: Text, val deadline: Long)

    init {
        scope.launch { for (cmd in inbox) handle(cmd) }
        scope.launch {
            for (s in copies) {
                if (clipboard.writeText(s) is ClipWrite.Failed) noticeFlow.tryEmit(TextNotice.AutoCopyFailed)
            }
        }
    }

    val notices: SharedFlow<TextNotice> get() = noticeFlow

    /** 与 [peer] 的会话中的文字，按显示顺序（11.4、11.11）。 */
    fun conversation(peer: DeviceId): StateFlow<List<TextItem>> = conversationFlow(peer)

    /**
     * 用户点击“发送”或“发送剪贴板”（13.6）：先执行 TextRules.checkOutgoing；通过时生成新的消息 ID，写入 History_Store
     * （失败时仍然发送，E5），立即以“发送中”显示（11.11），然后在需要时打开 Session（9.2）并以 P1 优先级入队。
     * 从调用时起计时 10 s，建立 Session 的时间也算在内（11.6）。
     */
    suspend fun send(peer: DeviceId, text: String): SendCheck {
        val now = clock.monotonicMs()
        val utf8 = when (val c = TextRules.checkOutgoing(text)) {
            OutgoingCheck.Empty -> return SendCheck.Empty
            OutgoingCheck.HasUnpairedSurrogate -> return SendCheck.HasUnpairedSurrogate
            is OutgoingCheck.TooLong -> return SendCheck.TooLong(c.utf8Bytes)
            is OutgoingCheck.Ok -> c.utf8
        }
        val done = CompletableDeferred<MessageId>()
        inbox.send(Cmd.Send(peer, utf8, text, now, done))
        return SendCheck.Sent(done.await())
    }

    /** “重试”：相同的消息 ID 与文本，状态回到“发送中”，重新计时 10 s（11.7）；只对“发送失败”的消息有效。 */
    fun retry(id: MessageId) {
        inbox.trySend(Cmd.Retry(id, clock.monotonicMs()))
    }

    /** 分发器交来的应用消息；TEXT、TEXT_ACK、TEXT_CORRUPT 由本服务处理并返回 true，其他消息返回 false。 */
    fun onMessage(session: Session, msg: ProtocolMessage): Boolean {
        if (msg !is Text && msg !is TextAck && msg !is TextCorrupt) return false
        inbox.trySend(Cmd.Inbound(session, msg))
        return true
    }

    /** 启动时载入 History_Store 中的文字；“发送中”的计时已丢失，改为“发送失败”（M3.1、17.9）。 */
    suspend fun restoreOnStartup() = request { Cmd.Restore(it) }

    /** 删除记录（20.4）之后调用：会话中不再显示这些消息，之后迟到的应答也不再影响它们。 */
    suspend fun forget(keys: Set<TextKey>) = request { Cmd.Forget(keys, it) }

    /** 清空记录（20.5）：在 HistoryStore.clearAll 之后调用，清掉内存中的全部会话条目。已显示集合保留。 */
    suspend fun forgetAll() = request { Cmd.Forget(null, it) }

    /**
     * 一条文字的完整原文（通知中的“复制”，13.2、21.6）：先取会话中的条目（包括写库失败、只在内存中的消息，20.8），
     * 再查 History_Store。
     */
    fun originalText(peer: DeviceId, id: MessageId): String? {
        conversationFlow(peer).value.lastOrNull { it.key.messageId == id }?.let { return it.text }
        val r = history.findText(peer, id) ?: return null
        return StrictUtf8.decode(r.textUtf8.toByteArray())
    }

    /**
     * “以 .txt 文件发送”（11.13）：把 [text] 的 UTF‑8 字节（不加 BOM 或其他任何字节）写入缓存目录下随机命名的子目录中的
     * `文字_YYYYMMDD_HHMMSS.txt`；返回的 SourceRef 由调用方交给文件发送流程。前置条件：[text] 不含 Unpaired_Surrogate
     * （调用方先按 12.6、12.10 处理）。
     */
    fun writeTextFile(text: String): WriteResult<SourceRef> {
        require(!StrictUtf8.hasUnpairedSurrogate(text)) { "text must not contain unpaired surrogates" }
        val dir = Bytes.of(rng.nextBytes(16)).toHex()
        return textCache.write(dir, textFileName(clock.localNow()), StrictUtf8.encode(text))
    }

    private suspend fun request(make: (CompletableDeferred<Unit>) -> Cmd) {
        val d = CompletableDeferred<Unit>()
        inbox.send(make(d))
        d.await()
    }

    private fun conversationFlow(peer: DeviceId): MutableStateFlow<List<TextItem>> =
        synchronized(conversations) { conversations.getOrPut(peer) { MutableStateFlow(emptyList()) } }

    // ---- actor ----

    private fun handle(cmd: Cmd) {
        when (cmd) {
            is Cmd.Send -> {
                val id = MessageId(Bytes16.of(rng.nextBytes(16)))
                val key = TextKey(cmd.peer, id)
                val sentAt = clock.wallMs()
                val at = clock.recordTime()
                stored(history.appendText(TextRecord(0, cmd.peer, peerName(cmd.peer), id, Direction.SEND, cmd.utf8, sentAt, at, TextStatus.SENDING)))
                append(TextItem(key, Direction.SEND, cmd.text, sentAt, at, TextStatus.SENDING))
                step(TextEvent.Send(key, cmd.utf8, sentAt, cmd.now))
                cmd.done.complete(id)
            }
            is Cmd.Retry -> {
                val key = state.outgoing.keys.firstOrNull { it.messageId == cmd.id } ?: return
                step(TextEvent.Retry(key, cmd.now))
            }
            is Cmd.Inbound -> {
                val peer = cmd.session.peer.deviceId
                when (val m = cmd.msg) {
                    is TextAck -> step(TextEvent.AckReceived(TextKey(peer, m.messageId)))
                    is TextCorrupt -> step(TextEvent.CorruptReceived(TextKey(peer, m.messageId)))
                    is Text -> receive(cmd.session, m)
                    else -> Unit
                }
            }
            Cmd.Tick -> step(TextEvent.Tick(clock.monotonicMs()))
            is Cmd.Restore -> {
                restore()
                cmd.done.complete(Unit)
            }
            is Cmd.Forget -> {
                forgetKeys(cmd.keys)
                cmd.done.complete(Unit)
            }
        }
    }

    /** C1.12 接收方的处理顺序。 */
    private fun receive(session: Session, m: Text) {
        val peer = session.peer.deviceId
        // 1. 严格 UTF‑8 解码；非法时只回复 TEXT_CORRUPT：不保存、不显示，也不把消息 ID 记为已接收（12.7）
        val text = StrictUtf8.decode(m.text.toByteArray())
        if (text == null) {
            session.send(TextCorrupt(m.messageId))
            return
        }
        val key = TextKey(peer, m.messageId)
        val seen = shown[key]
        if (seen == true) {
            // 本次运行中已保存过：只回复 TEXT_ACK（11.8）
            session.send(TextAck(m.messageId))
            return
        }
        val name = session.peer.displayName
        val at = clock.recordTime()
        // 2–3. 去重并保存：直接插入，命中 UNIQUE (peer, message_id) 即为重复消息（11.8）
        when (val r = history.appendText(TextRecord(0, peer, name, m.messageId, Direction.RECEIVE, m.text, m.sentAt, at, TextStatus.RECEIVED))) {
            StoreResult.Ok, StoreResult.Duplicate -> {
                // 4. 已保存后才回复 TEXT_ACK（11.12），然后显示、通知、自动复制；重复消息只回复 ACK
                session.send(TextAck(m.messageId))
                shown[key] = true
                if (r == StoreResult.Ok && seen == null) show(key, name, text, m.sentAt, at)
            }
            is StoreResult.Failed -> {
                noticeFlow.tryEmit(TextNotice.StoreFailed(r.detail))
                if (history.findText(peer, m.messageId) != null) {
                    // 写入失败但记录已存在（此前已保存）：按重复消息处理（11.8）
                    session.send(TextAck(m.messageId))
                    shown[key] = true
                    return
                }
                // 写入失败：不回复 ACK，但仍然显示；发送方重试时由已显示集合挡住重复显示（20.8）
                if (seen == null) {
                    shown[key] = false
                    show(key, name, text, m.sentAt, at)
                }
            }
        }
    }

    /** 显示一条新收到的文字；处于 Background_State 时发通知（21.6），开启“自动复制”时排队写剪贴板（13.8）。 */
    private fun show(key: TextKey, peerName: String, text: String, sentAt: Long, at: RecordTime) {
        append(TextItem(key, Direction.RECEIVE, text, sentAt, at, TextStatus.RECEIVED))
        if (lifecycle.background.value) notifier.text(NotifyKey.Text(key.peer, key.messageId), peerName, TextRules.notificationPreview(text))
        if (settings.current().autoCopy) copies.trySend(text)
    }

    private fun append(item: TextItem) = conversationFlow(item.key.peer).update { it + item }

    private fun restore() {
        val records = when (val r = history.list()) {
            is ListResult.Failed -> {
                noticeFlow.tryEmit(TextNotice.StoreFailed(r.detail))
                return
            }
            is ListResult.Ok -> r.items.filterIsInstance<HistoryRecord.Text>().map { it.record }
        }
        val byPeer = LinkedHashMap<DeviceId, MutableList<TextItem>>()
        val usable = ArrayList<TextRecord>(records.size)
        for (r in records) {
            val text = StrictUtf8.decode(r.textUtf8.toByteArray()) ?: continue
            usable += r
            byPeer.getOrPut(r.peer) { ArrayList() } += TextItem(TextKey(r.peer, r.messageId), r.direction, text, r.sentAtUtcMs, r.recordedAt, r.status)
        }
        for ((peer, items) in byPeer) conversationFlow(peer).update { items + it }
        step(TextEvent.Restored(usable))
    }

    private fun forgetKeys(keys: Set<TextKey>?) {
        if (keys == null) {
            step(TextEvent.Forget(state.outgoing.keys))
            synchronized(conversations) { conversations.values.toList() }.forEach { it.value = emptyList() }
            return
        }
        step(TextEvent.Forget(keys))
        for (peer in keys.mapTo(HashSet()) { it.peer }) conversationFlow(peer).update { items -> items.filter { it.key !in keys } }
    }

    private fun step(e: TextEvent) {
        val s = TextReducer.reduce(state, e)
        state = s.state
        s.effects.forEach(::execute)
    }

    private fun execute(f: TextEffect) {
        when (f) {
            is TextEffect.Transmit -> {
                val deadline = state.outgoing[TextKey(f.peer, f.msg.messageId)]?.deadline ?: return
                outbox(f.peer).trySend(Outgoing(f.msg, deadline))
            }
            is TextEffect.SaveStatus -> {
                stored(history.updateTextStatus(f.key.messageId, f.key.peer, f.status))
                conversationFlow(f.key.peer).update { items ->
                    items.map { if (it.key == f.key && it.direction == Direction.SEND) it.copy(status = f.status) else it }
                }
            }
            is TextEffect.ArmTimer -> scope.launch {
                // 等到单调时钟确实到达截止时刻再 Tick（delay 与时钟的取整可能相差 1 ms）
                while (true) {
                    val left = f.atMono - clock.monotonicMs()
                    if (left <= 0) break
                    delay(left)
                }
                inbox.send(Cmd.Tick)
            }
        }
    }

    private fun outbox(peer: DeviceId): Channel<Outgoing> = outboxes.getOrPut(peer) {
        Channel<Outgoing>(Channel.UNLIMITED).also { ch -> scope.launch { for (o in ch) transmit(peer, o) } }
    }

    private suspend fun transmit(peer: DeviceId, o: Outgoing) {
        // 已超过 10 s（已标为“发送失败”）的不再发出，由用户重试（11.6、11.7）
        if (clock.monotonicMs() >= o.deadline) return
        when (val r = sessions.open(Target.Device(peer))) {
            is OpenResult.Ok -> r.session.send(o.msg, Priority.TEXT)
            is OpenResult.Failed -> noticeFlow.tryEmit(TextNotice.ConnectFailed(peer, r.failure))
            // 本机已不信任对方（例如刚解除配对）：不在这里配对，直接关闭
            is OpenResult.Pairing -> r.connection.close()
        }
    }

    private fun stored(r: StoreResult) {
        if (r is StoreResult.Failed) noticeFlow.tryEmit(TextNotice.StoreFailed(r.detail))
    }

    /** 记录中的对方名称快照（20.1）：可信设备名称，其次是当前 Session 中的名称。 */
    private fun peerName(peer: DeviceId): String =
        trust.get(peer)?.displayName ?: sessions.sessions.value[peer]?.peer?.displayName ?: ""

    companion object {
        /** `文字_YYYYMMDD_HHMMSS.txt`（C1.12）。 */
        fun textFileName(now: LocalDateTime): String = String.format(
            Locale.ROOT, "文字_%04d%02d%02d_%02d%02d%02d.txt", now.year, now.monthValue, now.dayOfMonth, now.hour, now.minute, now.second,
        )
    }
}

/** 当前时刻的记录时间：UTC 毫秒与本机当时的 UTC 偏移（分钟，20.1）。 */
internal fun Clock.recordTime(): RecordTime {
    val utc = wallMs()
    val local = localNow().toInstant(ZoneOffset.UTC).toEpochMilli()
    return RecordTime(utc, Math.floorDiv(local - utc + 30_000, 60_000L).toInt())
}
