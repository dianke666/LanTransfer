@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.Direction
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.TextKey
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.NotifyEvent
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.StrictUtf8
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TextCorrupt
import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.random.Random

private const val KEYS = 4

class TextPropertiesTest : FunSpec({
    test("Property 39: 文字接收恰好一次") {
        // 两个发送方共用同一组消息 ID（检查按（发送方, 消息 ID）区分）；每个 ID 一份合法正文、一份非法正文。
        // 动作：投递（75% 合法）、切换 History_Store 写入失败、切换前后台、切换自动复制。参考模型按 C1.12 编写。
        checkAll(Pbt.iterations(100), Arb.list(Gens.unicodeText(8), KEYS..KEYS), Arb.list(Gens.invalidUtf8(), KEYS..KEYS), Arb.long()) { bodies, invalid, seed ->
            runTest {
                val rnd = Random(seed)
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val b = TextDevice(TestDevice("B", 2, iface("wlan0", 192, 168, 1, 3), net, clock, backgroundScope).also { it.listen() }, clock, backgroundScope, 2)
                val senders = listOf(
                    TestDevice("A", 1, iface("wlan0", 192, 168, 1, 2), net, clock, backgroundScope),
                    TestDevice("C", 3, iface("wlan0", 192, 168, 1, 4), net, clock, backgroundScope),
                )
                for (s in senders) {
                    s.trusts(b.d)
                    b.d.trusts(s)
                }
                runCurrent()

                var failing = false
                var background = false
                var autoCopy = false
                b.db.failWrites = { op -> failing && op == "appendText" }

                // 参考模型
                val store = HashSet<TextKey>()
                val shown = HashMap<TextKey, Boolean>()
                val expReplies = senders.map { ArrayList<ProtocolMessage>() }
                val expItems = senders.map { ArrayList<String>() }
                val expNotify = ArrayList<NotifyKey>()
                val expCopies = ArrayList<String>()

                // 此前运行中已保存的记录
                for (s in senders) for (k in 0 until KEYS) {
                    if (rnd.nextInt(5) != 0) continue
                    val key = TextKey(s.id, messageIdOf(k + 1))
                    b.history.appendText(TextRecord(0, s.id, s.name, key.messageId, Direction.RECEIVE, Bytes.of(StrictUtf8.encode(bodies[k])), 1, RecordTime(1, 480), TextStatus.RECEIVED))
                    store += key
                }

                val sessions = senders.map { (it.manager.open(Target.Device(b.d.id)) as OpenResult.Ok).session }
                val replies = senders.map { ArrayList<ProtocolMessage>() }
                sessions.forEachIndexed { i, s -> backgroundScope.launch { for (m in s.incoming) replies[i] += m } }
                runCurrent()

                val log = ArrayList<String>()
                repeat(rnd.nextInt(1, 40)) {
                    when (rnd.nextInt(10)) {
                        0 -> failing = !failing
                        1 -> {
                            background = !background
                            b.lifecycle.setBackground(background)
                        }
                        2 -> {
                            autoCopy = !autoCopy
                            b.settings.update { it.copy(autoCopy = autoCopy) }
                        }
                        else -> {
                            val i = rnd.nextInt(senders.size)
                            val k = rnd.nextInt(KEYS)
                            val valid = rnd.nextInt(4) != 0
                            log += "deliver(s=$i, k=$k, valid=$valid, failing=$failing, bg=$background, copy=$autoCopy)"
                            val id = messageIdOf(k + 1)
                            sessions[i].send(rawText(k + 1, if (valid) StrictUtf8.encode(bodies[k]) else invalid[k].bytes), Priority.TEXT)
                            runCurrent()
                            val key = TextKey(senders[i].id, id)
                            fun display() {
                                expItems[i] += bodies[k]
                                if (background) expNotify += NotifyKey.Text(key.peer, key.messageId)
                                if (autoCopy) expCopies += bodies[k]
                            }
                            val seen = shown[key]
                            when {
                                !valid -> expReplies[i] += TextCorrupt(id)
                                seen == true -> expReplies[i] += TextAck(id)
                                key in store -> {
                                    expReplies[i] += TextAck(id)
                                    shown[key] = true
                                }
                                failing -> if (seen == null) {
                                    display()
                                    shown[key] = false
                                }
                                else -> {
                                    store += key
                                    expReplies[i] += TextAck(id)
                                    if (seen == null) display()
                                    shown[key] = true
                                }
                            }
                        }
                    }
                }
                runCurrent()

                withClue("seed=$seed\n${log.joinToString("\n")}") {
                    for (i in senders.indices) {
                        replies[i] shouldBe expReplies[i]
                        b.items(senders[i]).map { it.text } shouldBe expItems[i]
                        // 只在写入 History_Store 之后回复 TEXT_ACK
                        replies[i].filterIsInstance<TextAck>().forEach { b.history.findText(senders[i].id, it.messageId).shouldNotBeNull() }
                    }
                    b.notifier.events.map { (it as NotifyEvent.Text).key } shouldBe expNotify
                    b.clipboard.writes shouldBe expCopies
                    b.clipboard.content shouldBe expCopies.lastOrNull()
                    // 非法正文、写入失败都不留下记录
                    val saved = (b.history.list() as ListResult.Ok).items.filterIsInstance<HistoryRecord.Text>().map { TextKey(it.record.peer, it.record.messageId) }
                    saved.toSet() shouldBe store
                }
            }
        }
    }
})
