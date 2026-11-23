@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.PairingResult
import com.lantransfer.domain.PairingRole
import com.lantransfer.domain.PairingTokenRegistry
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.SeededRng
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.QrCodec
import com.lantransfer.protocol.QrDecodeResult
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.TextAck
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContain
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.shouldNotBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private val ifA = iface("wlan0", 192, 168, 1, 2)
private val ifB = iface("wlan0", 192, 168, 1, 3)
private val ifC = iface("wlan0", 192, 168, 1, 4)

/** 一台带 PairingService 的测试设备；[notices] 收集全部配对提示，[qr] 收集二维码界面输出（调用 [showQr] 后）。 */
private class PairDevice(val d: TestDevice, clock: FakeClock, private val scope: CoroutineScope, seed: Long) {
    val tokens = PairingTokenRegistry(SeededRng(seed))
    val cancelled = ArrayList<DeviceId>()
    val service = PairingService(
        d.manager, d.trust, tokens, clock, SeededRng(seed + 100), scope, d.identity, d.transport.monitor,
        listenPort = { d.port.takeIf { it != 0 } },
        cancelTransfers = { cancelled += it },
    )
    val notices = ArrayList<PairingNotice>()
    val qr = ArrayList<QrScreenState>()

    init {
        scope.launch { service.notices.collect { notices += it } }
    }

    fun showQr() {
        scope.launch { service.qrScreen().collect { qr += it } }
    }

    val lastQr: QrScreenState.Showing get() = qr.last() as QrScreenState.Showing

    suspend fun awaitRequest(): PairingUiState.Request =
        service.state.first { it is PairingUiState.Request && it.sas != null } as PairingUiState.Request
}

private class World(scope: TestScope) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = PairDevice(TestDevice("A", 1, ifA, net, clock, scope.backgroundScope), clock, scope.backgroundScope, 1)
    val b = PairDevice(TestDevice("B", 2, ifB, net, clock, scope.backgroundScope).also { it.listen() }, clock, scope.backgroundScope, 2)

    fun c(scope: TestScope) = PairDevice(TestDevice("C", 3, ifC, net, clock, scope.backgroundScope), clock, scope.backgroundScope, 3)

    val bAddress: Target get() = Target.Address(ifB.address, b.d.port)
}

private fun TestScope.advance(ms: Long) {
    testScheduler.advanceTimeBy(ms)
    testScheduler.runCurrent()
}

private fun finished(p: PairDevice) = p.notices.filterIsInstance<PairingNotice.Finished>().map { it.done.result }

private fun retoken(text: String, token: Bytes16? = null, fingerprint: Bytes? = null): String {
    val p = (QrCodec.decode(text) as QrDecodeResult.Ok).payload
    return QrCodec.encode(p.copy(token = token ?: p.token, fingerprint = fingerprint ?: p.fingerprint))
}

class PairingServiceTest : FunSpec({
    context("点选配对（需求 7）") {
        test("双方验证码一致，接收方接受后两端写入信任并建立 Session；只有发起方记录对方地址") {
            runTest {
                val w = World(this)
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                val req = w.b.awaitRequest()
                val code = w.a.service.state.first { it is PairingUiState.ShowingCode } as PairingUiState.ShowingCode
                req.sas shouldBe code.sas
                req.peer.deviceId shouldBe w.a.d.id
                w.b.service.respond(req.id, accept = true)
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
                runCurrent()
                val ta = w.a.d.trust.get(w.b.d.id).shouldNotBeNull()
                ta.publicKey shouldBe w.b.d.publicKey
                ta.lastAddress shouldBe ifB.address
                ta.lastPort shouldBe w.b.d.port
                ta.autoAccept shouldBe false
                val tb = w.b.d.trust.get(w.a.d.id).shouldNotBeNull()
                tb.publicKey shouldBe w.a.d.publicKey
                tb.lastAddress.shouldBeNull()
                val sa = w.a.d.manager.session(w.b.d.id).shouldNotBeNull()
                val sb = w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                val ack = TextAck(MessageId(Bytes16.of(ByteArray(16) { 9 })))
                sa.send(ack)
                sb.incoming.receive() shouldBe ack
                finished(w.b) shouldBe listOf(PairingResult.SUCCESS)
                w.a.service.state.value shouldBe PairingUiState.Idle
                w.b.service.state.value shouldBe PairingUiState.Idle
            }
        }

        test("手动连接的目标已是可信设备：直接得到 Session，不出现配对界面（6.6）") {
            runTest {
                val w = World(this)
                w.a.d.trusts(w.b.d)
                w.b.d.trusts(w.a.d)
                runCurrent()
                w.a.service.pair(w.bAddress).shouldBeInstanceOf<PairingOutcome.AlreadyTrusted>()
                runCurrent()
                w.a.notices shouldBe emptyList()
                w.b.notices shouldBe emptyList()
            }
        }

        test("拒绝后 60 s 内同一设备的请求被自动拒绝且不弹出界面；满 60 s 后重新显示（7.6、7.8）") {
            runTest {
                val w = World(this)
                runCurrent()
                val first = async { w.a.service.pair(w.bAddress) }
                w.b.service.respond(w.b.awaitRequest().id, accept = false)
                first.await() shouldBe PairingOutcome.Done(PairingResult.REJECTED)
                runCurrent()
                w.a.service.pair(w.bAddress) shouldBe PairingOutcome.Done(PairingResult.REJECTED)
                runCurrent()
                w.b.notices shouldContain PairingNotice.AutoRejected(w.a.d.id, PairingResult.AUTO_REJECTED)
                w.b.service.state.value shouldBe PairingUiState.Idle
                advance(60_000)
                val third = async { w.a.service.pair(w.bAddress) }
                w.b.service.respond(w.b.awaitRequest().id, accept = false)
                third.await() shouldBe PairingOutcome.Done(PairingResult.REJECTED)
                w.a.d.trust.get(w.b.d.id).shouldBeNull()
                w.b.d.trust.get(w.a.d.id).shouldBeNull()
            }
        }

        test("60 s 无人响应：第 59 999 ms 仍在等待，满 60 s 时双方都以超时结束（7.7）") {
            runTest {
                val w = World(this)
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                w.b.awaitRequest()
                val t0 = testScheduler.currentTime
                advance(59_999 - (testScheduler.currentTime - t0))
                outcome.isActive shouldBe true
                w.b.service.state.value.shouldBeInstanceOf<PairingUiState.Request>()
                advance(1)
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.TIMEOUT)
                runCurrent()
                w.b.service.state.value shouldBe PairingUiState.Idle
                finished(w.b) shouldBe listOf(PairingResult.TIMEOUT)
            }
        }

        test("接收方正在显示请求时，第三台设备的请求被回复 BUSY（7.9）") {
            runTest {
                val w = World(this)
                val c = w.c(this)
                runCurrent()
                val fromA = async { w.a.service.pair(w.bAddress) }
                val req = w.b.awaitRequest()
                c.service.pair(w.bAddress) shouldBe PairingOutcome.Done(PairingResult.BUSY)
                runCurrent()
                w.b.notices shouldContain PairingNotice.AutoRejected(c.d.id, PairingResult.BUSY)
                w.b.service.state.value.shouldBeInstanceOf<PairingUiState.Request>().id shouldBe req.id
                w.b.service.respond(req.id, accept = true)
                fromA.await() shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
            }
        }

        test("发起方取消：接收方关闭确认界面，双方都不保存信任（7.10）") {
            runTest {
                val w = World(this)
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                w.b.awaitRequest()
                w.a.service.cancel()
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.CANCELLED)
                runCurrent()
                w.b.service.state.value shouldBe PairingUiState.Idle
                finished(w.b) shouldBe listOf(PairingResult.CANCELLED)
                w.a.d.trust.get(w.b.d.id).shouldBeNull()
                w.b.d.trust.get(w.a.d.id).shouldBeNull()
            }
        }

        test("发起方保存信任失败（E5）：连接关闭，接收方丢弃暂存信任（7.12）") {
            runTest {
                val w = World(this)
                w.a.d.trust.failWrites = { it == "upsert" }
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                w.b.service.respond(w.b.awaitRequest().id, accept = true)
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.STORE_FAILED)
                runCurrent()
                finished(w.b) shouldBe listOf(PairingResult.DISCONNECTED)
                w.b.d.trust.get(w.a.d.id).shouldBeNull()
                w.a.d.manager.sessions.value shouldBe emptyMap()
                w.b.d.manager.sessions.value shouldBe emptyMap()
            }
        }

        test("确认界面显示期间连接中断：接收方关闭界面，发起方得到 DISCONNECTED（7.12）") {
            runTest {
                val w = World(this)
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                w.b.awaitRequest()
                w.net.connections.single().reset()
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.DISCONNECTED)
                runCurrent()
                w.b.service.state.value shouldBe PairingUiState.Idle
                finished(w.b) shouldBe listOf(PairingResult.DISCONNECTED)
            }
        }

        test("连接失败时带回 OpenFailure") {
            runTest {
                val w = World(this)
                runCurrent()
                w.a.service.pair(Target.Address(ifC.address, 42580)) shouldBe PairingOutcome.Done(PairingResult.CONNECT_FAILED, OpenFailure.Unreachable)
            }
        }
    }

    context("扫码配对（需求 8）") {
        test("扫描成功：两端写入信任并建立 Session；令牌作废后二维码在 2 s 内刷新（8.4、8.7）") {
            runTest {
                val w = World(this)
                w.b.showQr()
                runCurrent()
                val text = w.b.lastQr.text
                w.a.service.pairByQr(text) shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
                runCurrent()
                w.a.d.trust.get(w.b.d.id).shouldNotBeNull().publicKey shouldBe w.b.d.publicKey
                w.b.d.trust.get(w.a.d.id).shouldNotBeNull().publicKey shouldBe w.a.d.publicKey
                w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                finished(w.b).single() shouldBe PairingResult.SUCCESS
                w.b.notices.filterIsInstance<PairingNotice.Finished>().single().done.role shouldBe PairingRole.QR_DISPLAY
                advance(2_000)
                w.b.lastQr.text shouldNotBe text
                w.a.d.manager.close(w.b.d.id, SessionCloseReason.NORMAL)
                runCurrent()
                w.a.service.pairByQr(text) shouldBe PairingOutcome.Done(PairingResult.TOKEN_INVALID) // 已使用的令牌
            }
        }

        test("令牌不符、超过 5 min、失败 5 次都使令牌失效，二维码随之刷新（8.6、8.9）") {
            runTest {
                val w = World(this)
                w.b.showQr()
                runCurrent()
                val text = w.b.lastQr.text
                w.a.service.pairByQr(retoken(text, token = Bytes16.of(ByteArray(16) { 7 }))) shouldBe PairingOutcome.Done(PairingResult.TOKEN_INVALID)
                runCurrent()
                w.b.d.trust.get(w.a.d.id).shouldBeNull()
                repeat(4) { w.a.service.pairByQr(retoken(text, token = Bytes16.of(ByteArray(16) { 8 }))) }
                runCurrent()
                w.a.service.pairByQr(text) shouldBe PairingOutcome.Done(PairingResult.TOKEN_INVALID) // 失败 5 次后正确的令牌也失效
                advance(1_000)
                val refreshed = w.b.lastQr.text
                refreshed shouldNotBe text
                advance(5 * 60_000L - 1_000)
                w.b.lastQr.text shouldBe refreshed // 5 min 内不刷新
                advance(1_000)
                val afterExpiry = w.b.lastQr.text
                afterExpiry shouldNotBe refreshed
                w.a.service.pairByQr(refreshed) shouldBe PairingOutcome.Done(PairingResult.TOKEN_INVALID) // 已过期
                w.a.service.pairByQr(afterExpiry) shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
            }
        }

        test("指纹不符时不发送令牌：令牌仍可用（8.10、10.7）") {
            runTest {
                val w = World(this)
                w.b.showQr()
                runCurrent()
                val text = w.b.lastQr.text
                w.a.service.pairByQr(retoken(text, fingerprint = Bytes.of(ByteArray(32)))) shouldBe
                    PairingOutcome.Done(PairingResult.CONNECT_FAILED, OpenFailure.FingerprintMismatch)
                runCurrent()
                w.b.notices shouldBe emptyList()
                w.b.lastQr.text shouldBe text
                w.a.service.pairByQr(text) shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
            }
        }

        test("无法解析的二维码文本直接失败；没有活动接口时显示 NoNetwork，接口恢复后重新显示（8.13）") {
            runTest {
                val w = World(this)
                w.a.service.pairByQr("LT1:!!!").shouldBeInstanceOf<PairingOutcome.InvalidQr>()
                w.b.d.transport.monitor.set(emptySet())
                w.b.showQr()
                runCurrent()
                w.b.qr.last() shouldBe QrScreenState.NoNetwork
                w.b.d.transport.monitor.set(setOf(ifB))
                advance(1_000)
                w.b.qr.last().shouldBeInstanceOf<QrScreenState.Showing>().payload.ipv4List shouldBe listOf(ifB.address.bits)
            }
        }
    }

    context("解除配对（9.3、10.8）") {
        test("删除信任、取消该对端的任务并发送 SESSION_CLOSE(UNPAIRED)，2 s 内完成") {
            runTest {
                val w = World(this)
                runCurrent()
                val outcome = async { w.a.service.pair(w.bAddress) }
                w.b.service.respond(w.b.awaitRequest().id, accept = true)
                outcome.await() shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
                runCurrent()
                val sb = w.b.d.manager.session(w.a.d.id).shouldNotBeNull()
                val start = testScheduler.currentTime
                w.a.service.unpair(w.b.d.id)
                (testScheduler.currentTime - start < 2_000) shouldBe true
                w.a.d.trust.get(w.b.d.id).shouldBeNull()
                w.a.cancelled shouldBe listOf(w.b.d.id)
                sb.closed.await() shouldBe CloseReason.PeerClosed(SessionCloseReason.UNPAIRED)
            }
        }
    }
})
