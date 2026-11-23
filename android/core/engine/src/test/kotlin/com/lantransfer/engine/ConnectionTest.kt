@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.PairCancel
import com.lantransfer.protocol.PairCancelReason
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.SessionClose
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TransferId
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainExactly
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.channels.toList
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private fun text(n: Int) = Text(MessageId(Bytes16.of(ByteArray(16) { n.toByte() })), 1_700_000_000_000, Bytes.of("t$n".encodeToByteArray()))

private fun TestScope.advance(ms: Long) {
    testScheduler.advanceTimeBy(ms)
    testScheduler.runCurrent()
}

class ConnectionTest : FunSpec({
    context("Pending 阶段（C1.11、10.4）") {
        test("白名单消息照常交付、HEARTBEAT 不交付；第一条白名单以外的消息触发 NOT_TRUSTED 并关闭，之后什么都不交付") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                val req = PairRequest(Bytes.of(ByteArray(32) { 7 }))
                t.peer.send(req)
                t.peer.send(Heartbeat)
                t.peer.send(text(1))
                t.peer.send(PairCancel(PairCancelReason.USER))
                runCurrent()
                t.peer.receive() shouldBe SessionResult(SessionResultCode.NOT_TRUSTED)
                t.peer.receive().shouldBeNull() // 随后关闭
                t.conn.state.value shouldBe ConnState.Closed(CloseReason.PendingViolation)
                t.conn.incoming.toList() shouldContainExactly listOf(req)
            }
        }

        test("发起方收到 ACCEPTED 时由读循环切换为 Active，紧随其后的应用消息不算违规") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock, role = Role.INITIATOR)
                t.peer.send(SessionResult(SessionResultCode.ACCEPTED))
                t.peer.send(text(2))
                runCurrent()
                t.conn.state.value shouldBe ConnState.Active
                t.conn.incoming.receive() shouldBe SessionResult(SessionResultCode.ACCEPTED)
                t.conn.incoming.receive() shouldBe text(2)
            }
        }

        test("接收方收到 PAIR_COMPLETE 时切换为 Active") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                t.peer.send(PairComplete)
                t.peer.send(text(3))
                runCurrent()
                t.conn.state.value shouldBe ConnState.Active
                t.conn.incoming.receive() shouldBe PairComplete
                t.conn.incoming.receive() shouldBe text(3)
            }
        }
    }

    context("记录层与解码错误（10.5、22.6）") {
        test("IntegrityError：之前的消息照常交付，之后的字节不再处理，连接以 IntegrityFailed 关闭") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                t.conn.activate()
                t.peer.send(text(1))
                t.peer.sendRaw(byteArrayOf(0x00, 0x20) + ByteArray(32)) // 长度合法但无法解密的 record
                t.peer.send(text(2))
                runCurrent()
                t.conn.state.value shouldBe ConnState.Closed(CloseReason.IntegrityFailed)
                t.conn.incoming.toList() shouldContainExactly listOf(text(1))
                t.peer.receive().shouldBeNull()
            }
        }

        test("DecodeError：之前的消息照常交付，之后的 record 不再解析，连接以 ProtocolError 关闭") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                t.conn.activate()
                t.peer.send(text(1))
                t.peer.sendPlain(byteArrayOf(0x12, 0x34, 0x20, 0, 0, 0, 0)) // magic 不符
                t.peer.send(text(2))
                runCurrent()
                t.conn.state.value.shouldBeInstanceOf<ConnState.Closed>().reason.shouldBeInstanceOf<CloseReason.ProtocolError>()
                t.conn.incoming.toList() shouldContainExactly listOf(text(1))
            }
        }
    }

    context("关闭") {
        test("对方 SESSION_CLOSE → PeerClosed(reason)；EOF → PeerClosed(null)") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                t.peer.send(SessionClose(SessionCloseReason.APP_EXIT))
                runCurrent()
                t.conn.closed.await() shouldBe CloseReason.PeerClosed(SessionCloseReason.APP_EXIT)
                CloseReason.PeerClosed(SessionCloseReason.APP_EXIT).interruptReason shouldBe com.lantransfer.domain.InterruptReason.PEER_CLOSED
                CloseReason.PeerClosed(SessionCloseReason.DUPLICATE).interruptReason.shouldBeNull()

                val t2 = connectionUnderTest(backgroundScope, clock)
                t2.peer.sock.close()
                runCurrent()
                t2.conn.closed.await() shouldBe CloseReason.PeerClosed(null)
            }
        }

        test("主动关闭：先写出已入队的 P0 消息和 SESSION_CLOSE，再关闭 TCP；之后入队失败") {
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                t.conn.activate()
                t.conn.enqueue(TextAck(MessageId(Bytes16.of(ByteArray(16)))), Priority.CONTROL)
                t.conn.closeGracefully(SessionCloseReason.NORMAL) shouldBe CloseReason.Local(SessionCloseReason.NORMAL)
                t.conn.enqueue(text(1), Priority.TEXT) shouldBe false
                t.peer.receive() shouldBe TextAck(MessageId(Bytes16.of(ByteArray(16))))
                t.peer.receive() shouldBe SessionClose(SessionCloseReason.NORMAL)
                t.peer.receive().shouldBeNull()
            }
        }
    }

    context("心跳与无响应（18.5、18.6）") {
        test("空闲满 5 s 才发 HEARTBEAT；对方 15 s 无任何帧时恰好在第 15 000 ms 以 PeerUnresponsive 关闭") {
            runTest {
                val clock = FakeClock(testScheduler)
                var rec: RecordingConn? = null
                val t = connectionUnderTest(backgroundScope, clock) { RecordingConn(it, clock).also { r -> rec = r } }
                advance(4_999)
                rec!!.writeTimes shouldBe emptyList()
                advance(1)
                rec!!.writeTimes shouldContainExactly listOf(5_000L)
                t.peer.receive() shouldBe Heartbeat
                advance(9_999)
                t.conn.state.value shouldBe ConnState.Pending
                rec!!.writeTimes shouldContainExactly listOf(5_000L, 10_000L)
                advance(1)
                t.conn.state.value shouldBe ConnState.Closed(CloseReason.PeerUnresponsive)
                clock.monotonicMs() shouldBe 15_000L
            }
        }

        test("持续有数据发出时不发心跳；对方每 10 s 发 1 次心跳时连接保持") {
            runTest {
                val clock = FakeClock(testScheduler)
                var rec: RecordingConn? = null
                val t = connectionUnderTest(backgroundScope, clock) { RecordingConn(it, clock).also { r -> rec = r } }
                t.conn.activate()
                repeat(10) { i ->
                    advance(3_000)
                    t.conn.enqueue(FileChunk(TransferId(Bytes16.of(ByteArray(16))), 0u, i.toULong(), Bytes.of(byteArrayOf(1))), Priority.CONTROL)
                    if (i % 3 == 2) t.peer.send(Heartbeat)
                    runCurrent()
                }
                rec!!.writeTimes shouldContainExactly (1..10).map { it * 3_000L }
                repeat(10) { t.peer.receive().shouldBeInstanceOf<FileChunk>() }
                t.conn.state.value shouldBe ConnState.Active
            }
        }
    }
})
