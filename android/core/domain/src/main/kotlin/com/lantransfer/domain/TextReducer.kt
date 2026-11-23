package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.Text

/** 一条发出的文字由（对方设备 ID, 消息 ID）确定。 */
data class TextKey(val peer: DeviceId, val messageId: MessageId)

/** 发出的文字及其发送状态（M3.1）。[deadline] 只在 SENDING 时非空：从点击起计时 10 s（11.6、11.11）。 */
data class OutgoingText(val key: TextKey, val text: Bytes, val sentAtUtcMs: Long, val status: TextStatus, val deadline: Long?) {
    /** FAILED 时始终显示“重试”（11.6、11.7）。 */
    val showRetry: Boolean get() = status == TextStatus.FAILED
}

data class TextState(val outgoing: Map<TextKey, OutgoingText> = emptyMap())

sealed interface TextEvent {
    /** 发送前检查已通过、记录已写入 History_Store（C4.4）。 */
    data class Send(val key: TextKey, val text: Bytes, val sentAtUtcMs: Long, val now: Long) : TextEvent

    data class Retry(val key: TextKey, val now: Long) : TextEvent

    data class AckReceived(val key: TextKey) : TextEvent

    data class CorruptReceived(val key: TextKey) : TextEvent

    data class Tick(val now: Long) : TextEvent

    /** 应用启动时从 History_Store 载入的发出记录：SENDING 的计时已丢失，改为 FAILED（M3.1、17.9 同理）。 */
    data class Restored(val records: List<TextRecord>) : TextEvent

    /** 记录被删除或清空（20.4、20.5）：之后迟到的应答不再影响任何记录。 */
    data class Forget(val keys: Set<TextKey>) : TextEvent
}

sealed interface TextEffect {
    /** 需要时打开 Session，以 P1 优先级入队 TEXT（C4.4）。 */
    data class Transmit(val peer: DeviceId, val msg: Text) : TextEffect

    data class SaveStatus(val key: TextKey, val status: TextStatus) : TextEffect

    data class ArmTimer(val atMono: Long) : TextEffect
}

/** 文字发送状态机（M3.1，需求 11.5–11.7、12.12）。DELIVERED 之后不再变化；未知消息的应答一律忽略。 */
object TextReducer {
    const val ACK_TIMEOUT_MS = 10_000L

    fun reduce(s: TextState, e: TextEvent): Step<TextState, TextEffect> = when (e) {
        is TextEvent.Send -> {
            if (e.key in s.outgoing) {
                Step(s, emptyList())
            } else {
                val t = OutgoingText(e.key, e.text, e.sentAtUtcMs, TextStatus.SENDING, e.now + ACK_TIMEOUT_MS)
                Step(s.with(t), listOf(transmit(t), TextEffect.ArmTimer(e.now + ACK_TIMEOUT_MS)))
            }
        }
        is TextEvent.Retry -> {
            val t = s.outgoing[e.key]
            if (t == null || t.status != TextStatus.FAILED) {
                Step(s, emptyList())
            } else {
                val next = t.copy(status = TextStatus.SENDING, deadline = e.now + ACK_TIMEOUT_MS)
                Step(s.with(next), listOf(TextEffect.SaveStatus(e.key, TextStatus.SENDING), transmit(next), TextEffect.ArmTimer(e.now + ACK_TIMEOUT_MS)))
            }
        }
        is TextEvent.AckReceived -> change(s, e.key, TextStatus.DELIVERED) { it == TextStatus.SENDING || it == TextStatus.FAILED }
        is TextEvent.CorruptReceived -> change(s, e.key, TextStatus.FAILED) { it == TextStatus.SENDING }
        is TextEvent.Tick -> {
            val expired = s.outgoing.values.filter { it.status == TextStatus.SENDING && it.deadline != null && e.now >= it.deadline }
            if (expired.isEmpty()) {
                Step(s, emptyList())
            } else {
                val map = s.outgoing.toMutableMap()
                expired.forEach { map[it.key] = it.copy(status = TextStatus.FAILED, deadline = null) }
                Step(TextState(map), expired.map { TextEffect.SaveStatus(it.key, TextStatus.FAILED) })
            }
        }
        is TextEvent.Restored -> {
            val map = s.outgoing.toMutableMap()
            val effects = mutableListOf<TextEffect>()
            for (r in e.records) {
                if (r.direction != Direction.SEND) continue
                val key = TextKey(r.peer, r.messageId)
                val status = if (r.status == TextStatus.SENDING) TextStatus.FAILED else r.status
                if (status != r.status) effects += TextEffect.SaveStatus(key, status)
                map[key] = OutgoingText(key, r.textUtf8, r.sentAtUtcMs, status, null)
            }
            Step(TextState(map), effects)
        }
        is TextEvent.Forget -> Step(TextState(s.outgoing - e.keys), emptyList())
    }

    private fun TextState.with(t: OutgoingText) = TextState(outgoing + (t.key to t))

    private fun transmit(t: OutgoingText) = TextEffect.Transmit(t.key.peer, Text(t.key.messageId, t.sentAtUtcMs, t.text))

    private inline fun change(s: TextState, key: TextKey, to: TextStatus, from: (TextStatus) -> Boolean): Step<TextState, TextEffect> {
        val t = s.outgoing[key]
        if (t == null || !from(t.status)) return Step(s, emptyList())
        return Step(s.with(t.copy(status = to, deadline = null)), listOf(TextEffect.SaveStatus(key, to)))
    }
}
