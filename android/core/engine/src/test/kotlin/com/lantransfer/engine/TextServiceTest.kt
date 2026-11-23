@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.Direction
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TextKey
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.engine.testing.NotifyEvent
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.StrictUtf8
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TextCorrupt
import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.collections.shouldContain
import io.kotest.matchers.collections.shouldHaveSize
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.shouldNotBe
import io.kotest.matchers.string.shouldMatch
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private fun TestScope.moveTo(t: Long) {
    val dt = t - testScheduler.currentTime
    if (dt > 0) testScheduler.advanceTimeBy(dt)
    testScheduler.runCurrent()
}

private fun sentId(r: SendCheck): MessageId = r.shouldBeInstanceOf<SendCheck.Sent>().id

private fun TextDevice.statusOf(peer: TestDevice, id: MessageId): TextStatus? = items(peer).singleOrNull { it.key.messageId == id }?.status

private fun TextDevice.textRecords(): List<TextRecord> = (history.list() as ListResult.Ok).items.filterIsInstance<HistoryRecord.Text>().map { it.record }

private fun utf8(s: String) = StrictUtf8.encode(s)

private fun okRef(r: WriteResult<SourceRef>): SourceRef = (r as? WriteResult.Ok)?.value ?: error("expected Ok, got $r")

class TextServiceTest : FunSpec({
    context("发送（11.3–11.7、11.11、12.12）") {
        test("立即以发送中显示；对方保存后回复 TEXT_ACK，发送方变为已送达，两端各保存 1 条") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                val body = "你好\r\n世界\u0000\t 👍🏽 "
                val id = sentId(w.a.service.send(w.b.d.id, body))
                w.a.items(w.b.d).single().status shouldBe TextStatus.SENDING
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                w.a.items(w.b.d).single().showRetry shouldBe false
                val got = w.b.items(w.a.d).single()
                got.text shouldBe body
                got.key shouldBe TextKey(w.a.d.id, id)
                got.direction shouldBe Direction.RECEIVE
                w.a.history.findText(w.b.d.id, id).shouldNotBeNull().status shouldBe TextStatus.DELIVERED
                val rb = w.b.history.findText(w.a.d.id, id).shouldNotBeNull()
                rb.status shouldBe TextStatus.RECEIVED
                rb.textUtf8 shouldBe Bytes.of(utf8(body))
                rb.peerName shouldBe "A"
                rb.recordedAt.offsetMin shouldBe 480
            }
        }

        test("10 s 阈值：对方不回应时 9 999 ms 仍为发送中，10 000 ms 变为发送失败；之后迟到的 TEXT_ACK 仍改为已送达") {
            runTest {
                val w = TextWorld(this, bDispatch = false)
                runCurrent()
                val t0 = testScheduler.currentTime
                val id = sentId(w.a.service.send(w.b.d.id, "hello"))
                moveTo(t0 + 9_999)
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.SENDING
                moveTo(t0 + 10_000)
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.FAILED
                w.a.items(w.b.d).single().showRetry shouldBe true
                w.a.history.findText(w.b.d.id, id).shouldNotBeNull().status shouldBe TextStatus.FAILED
                val sb = w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                sb.incoming.receive().shouldBeInstanceOf<Text>().messageId shouldBe id
                sb.send(TextAck(id))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                w.a.history.findText(w.b.d.id, id).shouldNotBeNull().status shouldBe TextStatus.DELIVERED
            }
        }

        test("无法建立 Session 时同样从点击起满 10 s 标为发送失败，并报告连接失败") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                val stranger = deviceIdOf(9)
                val t0 = testScheduler.currentTime
                val id = sentId(w.a.service.send(stranger, "x"))
                runCurrent()
                w.a.notices shouldContain TextNotice.ConnectFailed(stranger, OpenFailure.Unreachable)
                moveTo(t0 + 9_999)
                w.a.service.conversation(stranger).value.single().status shouldBe TextStatus.SENDING
                moveTo(t0 + 10_000)
                w.a.service.conversation(stranger).value.single().status shouldBe TextStatus.FAILED
                w.a.history.findText(stranger, id).shouldNotBeNull().status shouldBe TextStatus.FAILED
            }
        }

        test("重试：相同的消息 ID、文本与 sent_at，回到发送中并从重试起重新计时；记录只有 1 条（20.7）") {
            runTest {
                val w = TextWorld(this, bDispatch = false)
                runCurrent()
                val t0 = testScheduler.currentTime
                val id = sentId(w.a.service.send(w.b.d.id, "再试一次"))
                moveTo(t0 + 10_000)
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.FAILED
                val sb = w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                val first = sb.incoming.receive().shouldBeInstanceOf<Text>()
                moveTo(t0 + 13_000)
                w.a.service.retry(id)
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.SENDING
                w.a.history.findText(w.b.d.id, id).shouldNotBeNull().status shouldBe TextStatus.SENDING
                sb.incoming.receive() shouldBe first
                moveTo(t0 + 22_999)
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.SENDING
                moveTo(t0 + 23_000)
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.FAILED
                w.a.textRecords() shouldHaveSize 1
                w.a.items(w.b.d) shouldHaveSize 1
            }
        }

        test("收到 TEXT_CORRUPT 时立即标为发送失败（12.12）；对“发送中”以外的消息无效") {
            runTest {
                val w = TextWorld(this, bDispatch = false)
                runCurrent()
                val id = sentId(w.a.service.send(w.b.d.id, "bad?"))
                runCurrent()
                val sb = w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                sb.send(TextCorrupt(id))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.FAILED
                sb.send(TextAck(id))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                sb.send(TextCorrupt(id))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
            }
        }

        test("长度边界：UTF‑8 恰好 1,048,576 字节可以发送并原样送达，1,048,577 字节不发送（11.3、11.9）") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                val max = "a" + "中".repeat(349_525)
                StrictUtf8.encodedLength(max) shouldBe 1_048_576
                val id = sentId(w.a.service.send(w.b.d.id, max))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                w.b.items(w.a.d).single().text shouldBe max
                w.a.service.send(w.b.d.id, max + "a") shouldBe SendCheck.TooLong(1_048_577)
                w.a.service.send(w.b.d.id, "中".repeat(349_525) + "ab") shouldBe SendCheck.TooLong(1_048_577)
                w.a.service.send(w.b.d.id, "") shouldBe SendCheck.Empty
                w.a.service.send(w.b.d.id, "x\uD800y") shouldBe SendCheck.HasUnpairedSurrogate
                runCurrent()
                w.a.items(w.b.d) shouldHaveSize 1
                w.a.textRecords() shouldHaveSize 1
                w.b.items(w.a.d) shouldHaveSize 1
            }
        }

        test("发送时写库失败仍然发送，状态只保存在内存中并提示（E5、20.8）") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                w.a.db.failWrites = { true }
                val id = sentId(w.a.service.send(w.b.d.id, "仍然发送"))
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                w.a.history.findText(w.b.d.id, id).shouldBeNull()
                w.a.notices.filterIsInstance<TextNotice.StoreFailed>().shouldNotBe(emptyList<TextNotice.StoreFailed>())
                w.b.items(w.a.d).single().text shouldBe "仍然发送"
            }
        }
    }

    context("接收（11.4、11.8、11.12、13.8、20.8、21.6）") {
        test("开启自动复制：新消息按收到顺序串行写入剪贴板，最后一条留在剪贴板；重复消息与关闭后的消息不写入") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                w.b.settings.update { it.copy(autoCopy = true) }
                val s = w.openAB()
                s.send(rawText(1, utf8("一")))
                s.send(rawText(2, utf8("二\r\n")))
                s.send(rawText(3, utf8(" 三 ")))
                s.send(rawText(1, utf8("一")))
                runCurrent()
                w.b.clipboard.writes shouldBe listOf("一", "二\r\n", " 三 ")
                w.b.clipboard.content shouldBe " 三 "
                List(4) { s.incoming.receive() } shouldBe listOf(TextAck(messageIdOf(1)), TextAck(messageIdOf(2)), TextAck(messageIdOf(3)), TextAck(messageIdOf(1)))
                w.b.settings.update { it.copy(autoCopy = false) }
                s.send(rawText(4, utf8("四")))
                runCurrent()
                w.b.clipboard.writes shouldHaveSize 3
                w.b.items(w.a.d).map { it.text } shouldBe listOf("一", "二\r\n", " 三 ", "四")
            }
        }

        test("自动复制写入失败时提示（13.9）") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                w.b.settings.update { it.copy(autoCopy = true) }
                w.b.clipboard.failWrites = true
                w.openAB().send(rawText(1, utf8("x")))
                runCurrent()
                w.b.notices shouldContain TextNotice.AutoCopyFailed
            }
        }

        test("后台时每条新消息通知 1 次，预览为前 100 个码点加“…”；前台不通知（21.6）") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                val s = w.openAB()
                s.send(rawText(1, utf8("前台")))
                runCurrent()
                w.b.notifier.events.shouldBeEmpty()
                w.b.lifecycle.setBackground(true)
                val long = "𠀀".repeat(101)
                s.send(rawText(2, utf8(long)))
                s.send(rawText(2, utf8(long)))
                s.send(rawText(1, utf8("前台")))
                runCurrent()
                w.b.notifier.events shouldBe listOf(NotifyEvent.Text(NotifyKey.Text(w.a.d.id, messageIdOf(2)), "A", "𠀀".repeat(100) + "…"))
                w.b.service.originalText(w.a.d.id, messageIdOf(2)) shouldBe long
            }
        }

        test("写库失败：不回复 ACK 但显示 1 次并提示；存储恢复后发送方重试得到 ACK，不再显示（20.8、11.12）") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                w.b.settings.update { it.copy(autoCopy = true) }
                w.b.lifecycle.setBackground(true)
                w.b.db.failWrites = { it == "appendText" }
                val s = w.openAB()
                val m = rawText(1, utf8("存不下"))
                s.send(m)
                runCurrent()
                s.incoming.tryReceive().isFailure shouldBe true
                w.b.items(w.a.d).single().text shouldBe "存不下"
                w.b.notices.filterIsInstance<TextNotice.StoreFailed>() shouldHaveSize 1
                w.b.service.originalText(w.a.d.id, m.messageId) shouldBe "存不下"
                s.send(m)
                runCurrent()
                s.incoming.tryReceive().isFailure shouldBe true
                w.b.db.failWrites = { false }
                s.send(m)
                runCurrent()
                s.incoming.receive() shouldBe TextAck(m.messageId)
                w.b.history.findText(w.a.d.id, m.messageId).shouldNotBeNull()
                s.send(m)
                runCurrent()
                s.incoming.receive() shouldBe TextAck(m.messageId)
                w.b.items(w.a.d) shouldHaveSize 1
                w.b.notifier.events shouldHaveSize 1
                w.b.clipboard.writes shouldBe listOf("存不下")
            }
        }

        test("非法 UTF‑8 只回复 TEXT_CORRUPT，没有其他副作用；之后同一 ID 的合法消息按新消息处理（12.7）") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                w.b.settings.update { it.copy(autoCopy = true) }
                w.b.lifecycle.setBackground(true)
                val s = w.openAB()
                s.send(rawText(1, byteArrayOf(0x61, 0xC0.toByte(), 0x80.toByte())))
                runCurrent()
                s.incoming.receive() shouldBe TextCorrupt(messageIdOf(1))
                w.b.items(w.a.d).shouldBeEmpty()
                w.b.textRecords().shouldBeEmpty()
                w.b.notifier.events.shouldBeEmpty()
                w.b.clipboard.writes.shouldBeEmpty()
                s.isOpen shouldBe true
                s.send(rawText(1, utf8("ok")))
                runCurrent()
                s.incoming.receive() shouldBe TextAck(messageIdOf(1))
                w.b.items(w.a.d).single().text shouldBe "ok"
            }
        }

        test("History_Store 中已有的消息（此前运行中保存）只回复 TEXT_ACK，不显示（11.8）") {
            runTest {
                val w = TextWorld(this, aDispatch = false)
                runCurrent()
                val old = TextRecord(0, w.a.d.id, "A", messageIdOf(7), Direction.RECEIVE, Bytes.of(utf8("旧")), 1, RecordTime(1, 480), TextStatus.RECEIVED)
                w.b.history.appendText(old)
                val s = w.openAB()
                s.send(rawText(7, utf8("旧")))
                runCurrent()
                s.incoming.receive() shouldBe TextAck(messageIdOf(7))
                w.b.items(w.a.d).shouldBeEmpty()
                w.b.textRecords() shouldHaveSize 1
            }
        }
    }

    context("以 .txt 文件发送（11.13）") {
        test("内容逐字节等于文字的 UTF‑8 编码、不加 BOM；文件名为 文字_YYYYMMDD_HHMMSS.txt，同一秒内两次不冲突") {
            runTest {
                val w = TextWorld(this)
                val text = "第一行\r\n第二行\n\u0000\t末尾 "
                val r1 = okRef(w.a.service.writeTextFile(text))
                val r2 = okRef(w.a.service.writeTextFile(text))
                r1 shouldNotBe r2
                w.a.cache.dirNames shouldHaveSize 2
                for (r in listOf(r1, r2)) {
                    val e = w.a.sources.entry(r).shouldNotBeNull()
                    e.name shouldBe "文字_20251010_005320.txt"
                    e.name shouldMatch Regex("文字_\\d{8}_\\d{6}\\.txt")
                    e.data shouldBe Bytes.of(utf8(text))
                    e.data[0] shouldBe 0xE7.toByte() // “第”，前面没有 EF BB BF
                    w.a.sources.describe(r).shouldNotBeNull().size shouldBe utf8(text).size.toLong()
                }
                // 文字本身以 U+FEFF 开头时原样保留，不多也不少
                val r3 = okRef(w.a.service.writeTextFile("\uFEFFabc"))
                w.a.sources.entry(r3).shouldNotBeNull().data shouldBe Bytes.of(byteArrayOf(0xEF.toByte(), 0xBB.toByte(), 0xBF.toByte(), 0x61, 0x62, 0x63))
                w.a.cache.failWrites = true
                w.a.service.writeTextFile(text).shouldBeInstanceOf<WriteResult.Failed>()
            }
        }

        test("任意文字：文件内容等于其 UTF‑8 编码") {
            checkAll(Pbt.iterations(100), Gens.unicodeText()) { text ->
                runTest {
                    val w = TextWorld(this)
                    val r = okRef(w.a.service.writeTextFile(text))
                    w.a.sources.entry(r).shouldNotBeNull().data shouldBe Bytes.of(utf8(text))
                }
            }
        }
    }

    context("启动恢复与清空（M3.1、20.5）") {
        test("发送中的记录恢复为发送失败并可重试；清空后迟到的应答不再创建记录") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                val id = messageIdOf(5)
                val sending = TextRecord(0, w.b.d.id, "B", id, Direction.SEND, Bytes.of(utf8("上次没发完")), 1_000, RecordTime(1_000, 480), TextStatus.SENDING)
                val delivered = sending.copy(messageId = messageIdOf(6), status = TextStatus.DELIVERED)
                w.a.history.appendText(sending)
                w.a.history.appendText(delivered)
                w.a.service.restoreOnStartup()
                w.a.items(w.b.d).map { it.status } shouldBe listOf(TextStatus.FAILED, TextStatus.DELIVERED)
                w.a.history.findText(w.b.d.id, id).shouldNotBeNull().status shouldBe TextStatus.FAILED
                w.a.service.retry(id)
                runCurrent()
                w.a.statusOf(w.b.d, id) shouldBe TextStatus.DELIVERED
                w.b.items(w.a.d).single().text shouldBe "上次没发完"
                w.b.items(w.a.d).single().sentAtUtcMs shouldBe 1_000

                w.a.history.clearAll()
                w.a.service.forgetAll()
                w.a.items(w.b.d).shouldBeEmpty()
                w.b.d.manager.session(w.a.d.id).shouldNotBeNull().send(TextAck(id))
                runCurrent()
                w.a.items(w.b.d).shouldBeEmpty()
                w.a.history.findText(w.b.d.id, id).shouldBeNull()
            }
        }

        test("forget 只移除指定的消息") {
            runTest {
                val w = TextWorld(this)
                runCurrent()
                val one = sentId(w.a.service.send(w.b.d.id, "1"))
                val two = sentId(w.a.service.send(w.b.d.id, "2"))
                runCurrent()
                w.a.service.forget(setOf(TextKey(w.b.d.id, one)))
                w.a.items(w.b.d).map { it.key.messageId } shouldBe listOf(two)
                w.b.items(w.a.d).map { it.text } shouldBe listOf("1", "2")
                val unrelated: DeviceId = deviceIdOf(8)
                w.a.service.conversation(unrelated).value.shouldBeEmpty()
            }
        }
    }
})
