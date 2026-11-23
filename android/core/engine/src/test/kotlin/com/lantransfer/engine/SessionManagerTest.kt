@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.Ipv4
import com.lantransfer.engine.testing.ConnectBehavior
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.Fingerprint
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.PreambleCodec
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrPayload
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import com.lantransfer.protocol.TextAck
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private val ifA = iface("wlan0", 192, 168, 1, 2)
private val ifB = iface("wlan0", 192, 168, 1, 3)

private fun ack(n: Int) = TextAck(MessageId(Bytes16.of(ByteArray(16) { n.toByte() })))

/** 两台设备 A（ID 较小）与 B，B 已在监听。 */
private class Pair2(scope: TestScope, timings: SessionTimings = SessionTimings(), bVersion: ProtocolVersion = ProtocolVersion.CURRENT) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = TestDevice("A", 1, ifA, net, clock, scope.backgroundScope, timings = timings)
    val b = TestDevice("B", 2, ifB, net, clock, scope.backgroundScope, timings = timings, version = bVersion).also { it.listen() }

    fun mutualTrust() {
        a.trusts(b)
        b.trusts(a)
    }
}

private fun TestScope.advance(ms: Long) {
    testScheduler.advanceTimeBy(ms)
    testScheduler.runCurrent()
}

class SessionManagerTest : FunSpec({
    context("建立 Session") {
        test("双方互信：两端注册同一条连接、互发应用消息；记录最近成功地址；存在 Session 时加快轮询；重复 open 得到同一 Session") {
            runTest {
                val p = Pair2(this)
                p.mutualTrust()
                val s = p.a.manager.open(Target.Device(p.b.id)).shouldBeInstanceOf<OpenResult.Ok>().session
                runCurrent()
                val sb = p.b.manager.session(p.a.id).shouldNotBeNull()
                s.peer shouldBe PeerIdentity(p.b.id, "B", p.b.type, p.b.publicKey)
                sb.peer.deviceId shouldBe p.a.id
                s.connection.local shouldBe sb.connection.remote
                s.initiator shouldBe p.a.id
                sb.initiator shouldBe p.a.id
                s.send(ack(1)) shouldBe true
                sb.incoming.receive() shouldBe ack(1)
                sb.send(ack(2), Priority.TEXT) shouldBe true
                s.incoming.receive() shouldBe ack(2)
                p.a.manager.sessions.value.keys shouldBe setOf(p.b.id)
                p.b.manager.sessions.value.keys shouldBe setOf(p.a.id)
                p.a.trust.get(p.b.id)!!.lastAddress shouldBe ifB.address
                p.a.trust.get(p.b.id)!!.lastPort shouldBe 42580
                p.a.transport.monitor.fastPolling shouldBe true
                p.a.manager.open(Target.Device(p.b.id)) shouldBe OpenResult.Ok(s)
                p.net.connections.size shouldBe 1
            }
        }

        test("可信设备名称与握手中出示的不同时，认证后更新（9.6）；主动关闭后两端注销并恢复慢速轮询") {
            runTest {
                val p = Pair2(this)
                p.a.trusts(p.b, displayName = "旧名称")
                p.b.trusts(p.a)
                p.a.manager.open(Target.Device(p.b.id)).shouldBeInstanceOf<OpenResult.Ok>()
                runCurrent()
                p.a.trust.get(p.b.id)!!.displayName shouldBe "B"
                val sb = p.b.manager.session(p.a.id)!!
                p.a.manager.close(p.b.id, SessionCloseReason.NORMAL)
                sb.closed.await() shouldBe CloseReason.PeerClosed(SessionCloseReason.NORMAL)
                runCurrent()
                p.a.manager.sessions.value shouldBe emptyMap()
                p.b.manager.sessions.value shouldBe emptyMap()
                p.a.transport.monitor.fastPolling shouldBe false
            }
        }

        test("对方不可信：intent 为 PAIR，两端得到 Pending 连接；配对消息可往来；PAIR_COMPLETE 后两端 promote 为 Session") {
            runTest {
                val p = Pair2(this)
                val inbound = async { p.b.manager.pendingInbound.first() }
                val mine = p.a.manager.open(Target.Address(ifB.address, p.b.port)).shouldBeInstanceOf<OpenResult.Pairing>().connection
                mine.intent shouldBe Intent.PAIR
                mine.role shouldBe Role.INITIATOR
                val theirs = inbound.await()
                theirs.intent shouldBe Intent.PAIR
                theirs.peer.deviceId shouldBe p.a.id
                val req = PairRequest(Bytes.of(ByteArray(32) { 1 }))
                mine.send(req)
                theirs.incoming.receive() shouldBe req
                p.a.manager.sessions.value shouldBe emptyMap()
                mine.send(PairComplete)
                theirs.incoming.receive() shouldBe PairComplete
                val sb = theirs.promote().shouldNotBeNull()
                val sa = mine.promote().shouldNotBeNull()
                sa.initiator shouldBe p.a.id
                sb.initiator shouldBe p.a.id
                sa.send(ack(3))
                sb.incoming.receive() shouldBe ack(3)
                p.a.trust.get(p.b.id).shouldBeNull() // 信任由 PairingService 写入，SessionManager 不写
            }
        }

        test("Pending 阶段收到白名单以外的消息：接收方回复 NOT_TRUSTED 并关闭，配对消费者收不到它（10.4）") {
            runTest {
                val p = Pair2(this)
                val inbound = async { p.b.manager.pendingInbound.first() }
                val mine = p.a.manager.open(Target.Address(ifB.address, p.b.port)).shouldBeInstanceOf<OpenResult.Pairing>().connection
                val theirs = inbound.await()
                mine.send(ack(9))
                mine.incoming.receive() shouldBe SessionResult(SessionResultCode.NOT_TRUSTED)
                theirs.closed.await() shouldBe CloseReason.PendingViolation
                theirs.incoming.receiveCatching().getOrNull().shouldBeNull()
                mine.closed.await() shouldBe CloseReason.PeerClosed(null)
            }
        }
    }

    context("E1：每一种 OpenFailure") {
        test("NotLocal：公网地址与不在本机子网内的私有地址，立即失败且不发起任何连接（2.5）") {
            runTest {
                val p = Pair2(this)
                p.a.manager.open(Target.Address(Ipv4.of(8, 8, 8, 8), 42580)) shouldBe OpenResult.Failed(OpenFailure.NotLocal)
                p.a.manager.open(Target.Address(Ipv4.of(10, 0, 0, 9), 42580)) shouldBe OpenResult.Failed(OpenFailure.NotLocal)
                p.a.transport.connectAttempts.shouldBeEmpty()
                testScheduler.currentTime shouldBe 0L
            }
        }

        test("SelfConnection：目标是本机地址（不发包）；msg2 中的设备 ID 等于本机（6.9）") {
            runTest {
                val p = Pair2(this)
                p.a.manager.open(Target.Address(ifA.address, 42580)) shouldBe OpenResult.Failed(OpenFailure.SelfConnection)
                p.a.transport.connectAttempts.shouldBeEmpty()
                val twin = TestDevice("A2", 1, iface("wlan0", 192, 168, 1, 4), p.net, p.clock, backgroundScope, identityOverride = p.a.identity)
                twin.listen()
                p.a.manager.open(Target.Address(twin.iface.address, twin.port)) shouldBe OpenResult.Failed(OpenFailure.SelfConnection)
                runCurrent()
                twin.manager.sessions.value shouldBe emptyMap()
            }
        }

        test("BindFailed / Refused / Unreachable") {
            runTest {
                val p = Pair2(this)
                p.a.transport.connectBehavior = { if (it.port == 1) ConnectBehavior.BindFail(vpnSuspected = true) else null }
                p.a.manager.open(Target.Address(ifB.address, 1)) shouldBe OpenResult.Failed(OpenFailure.BindFailed(vpnSuspected = true))
                p.a.manager.open(Target.Address(ifB.address, 9)) shouldBe OpenResult.Failed(OpenFailure.Refused)
                p.a.manager.open(Target.Address(Ipv4.of(192, 168, 1, 77), 42580)) shouldBe OpenResult.Failed(OpenFailure.Unreachable)
                p.a.manager.open(Target.Device(deviceIdOf(55))) shouldBe OpenResult.Failed(OpenFailure.Unreachable) // 没有已知地址
            }
        }

        test("Timeout：TCP 无应答时 10 s 预算在第 9 999 ms 仍在等待，恰好第 10 000 ms 失败") {
            runTest {
                val p = Pair2(this)
                p.a.trusts(p.b)
                p.a.transport.connectBehavior = { ConnectBehavior.Hang }
                val r = async { p.a.manager.open(Target.Device(p.b.id)) }
                advance(9_999)
                r.isActive shouldBe true
                advance(1)
                r.await() shouldBe OpenResult.Failed(OpenFailure.Timeout)
                testScheduler.currentTime shouldBe 10_000L
            }
        }

        test("NoVersionResponse：TCP 已建立但对方不发前导，第 10 000 ms 失败；发了前导但不继续握手报 Timeout") {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val a = TestDevice("A", 1, ifA, net, clock, backgroundScope)
                val raw = net.host("raw", setOf(ifB))
                val l = raw.listen(listOf(42580))!!
                backgroundScope.launch { l.accept() } // 接受后什么也不发
                val r = async { a.manager.open(Target.Address(ifB.address, 42580)) }
                advance(9_999)
                r.isActive shouldBe true
                advance(1)
                r.await() shouldBe OpenResult.Failed(OpenFailure.NoVersionResponse)

                backgroundScope.launch { l.accept()!!.write(PreambleCodec.encode(ProtocolVersion.CURRENT)) }
                val r2 = async { a.manager.open(Target.Address(ifB.address, 42580)) }
                advance(10_000)
                r2.await() shouldBe OpenResult.Failed(OpenFailure.Timeout)
            }
        }

        test("VersionIncompatible：主版本不同，发起方失败，接收方发出提示事件（24.5）") {
            runTest {
                val p = Pair2(this, bVersion = ProtocolVersion(2, 0))
                val shown = async { p.b.manager.inboundFailures.first() }
                runCurrent()
                p.a.manager.open(Target.Address(ifB.address, p.b.port)) shouldBe
                    OpenResult.Failed(OpenFailure.VersionIncompatible(ProtocolVersion.V1_0, ProtocolVersion(2, 0)))
                shown.await().failure shouldBe OpenFailure.VersionIncompatible(ProtocolVersion(2, 0), ProtocolVersion.V1_0)
            }
        }

        test("ProtocolError：前导 magic 不符；握手消息长度为 0") {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val a = TestDevice("A", 1, ifA, net, clock, backgroundScope)
                val raw = net.host("raw", setOf(ifB))
                val l = raw.listen(listOf(42580))!!
                backgroundScope.launch { l.accept()!!.write("XXXX".encodeToByteArray() + byteArrayOf(1, 0)) }
                a.manager.open(Target.Address(ifB.address, 42580)) shouldBe OpenResult.Failed(OpenFailure.ProtocolError)
                backgroundScope.launch { l.accept()!!.write(PreambleCodec.encode(ProtocolVersion.CURRENT) + byteArrayOf(0, 0)) }
                a.manager.open(Target.Address(ifB.address, 42580)) shouldBe OpenResult.Failed(OpenFailure.ProtocolError)
            }
        }

        test("HandshakeAuthFailed：msg2 无法解密（10.6）") {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val a = TestDevice("A", 1, ifA, net, clock, backgroundScope)
                val raw = net.host("raw", setOf(ifB))
                val l = raw.listen(listOf(42580))!!
                backgroundScope.launch {
                    val c = l.accept()!!
                    c.write(PreambleCodec.encode(ProtocolVersion.CURRENT) + byteArrayOf(0, 100) + ByteArray(100) { 3 })
                }
                a.manager.open(Target.Address(ifB.address, 42580)) shouldBe OpenResult.Failed(OpenFailure.HandshakeAuthFailed)
            }
        }

        test("KeyMismatch：保存的公钥与出示的不一致，不发送 msg3，保存的公钥不变（9.4）") {
            runTest {
                val p = Pair2(this)
                val wrong = keyPairOf(99).publicKey
                p.a.trusts(p.b, key = wrong)
                p.b.trusts(p.a)
                p.a.manager.open(Target.Device(p.b.id)) shouldBe OpenResult.Failed(OpenFailure.KeyMismatch("B"))
                runCurrent()
                p.a.trust.get(p.b.id)!!.publicKey shouldBe wrong
                p.b.manager.sessions.value shouldBe emptyMap()
                p.a.manager.sessions.value shouldBe emptyMap()
            }
        }

        test("FingerprintMismatch：扫码时公钥指纹与二维码不符，不发送令牌（8.10、10.7）") {
            runTest {
                val p = Pair2(this)
                val qr = QrPayload(p.b.id, "B", listOf(ifB.address.bits), p.b.port, Bytes.of(ByteArray(32)), Bytes16.of(ByteArray(16) { 5 }))
                p.a.manager.open(Target.Qr(qr)) shouldBe OpenResult.Failed(OpenFailure.FingerprintMismatch)
            }
        }

        test("NotTrusted：对方不信任本机，本机信任保持不变（9.5）") {
            runTest {
                val p = Pair2(this)
                p.a.trusts(p.b)
                p.a.manager.open(Target.Device(p.b.id)) shouldBe OpenResult.Failed(OpenFailure.NotTrusted)
                p.a.trust.get(p.b.id).shouldNotBeNull()
                runCurrent()
                p.b.manager.sessions.value shouldBe emptyMap()
            }
        }

        test("PeerKeyMismatch：对方保存的本机公钥不一致；对方发出 KeyMismatch 提示") {
            runTest {
                val p = Pair2(this)
                p.a.trusts(p.b)
                p.b.trusts(p.a, key = keyPairOf(98).publicKey)
                val shown = async { p.b.manager.inboundFailures.first() }
                runCurrent()
                p.a.manager.open(Target.Device(p.b.id)) shouldBe OpenResult.Failed(OpenFailure.PeerKeyMismatch("B"))
                shown.await().failure shouldBe OpenFailure.KeyMismatch("A")
            }
        }
    }

    context("扫码目标（8.5）") {
        test("跳过不在本机子网内的候选地址；每个候选 5 s，两个都无应答时第 10 000 ms 失败") {
            runTest {
                val p = Pair2(this)
                val fp = Bytes.of(Fingerprint.of(p.b.publicKey.toByteArray()))
                val ips = listOf(Ipv4.of(8, 8, 8, 8), ifB.address, Ipv4.of(192, 168, 1, 9)).map { it.bits }
                p.a.transport.connectBehavior = { ConnectBehavior.Hang }
                val r = async { p.a.manager.open(Target.Qr(QrPayload(p.b.id, "B", ips, p.b.port, fp, Bytes16.of(ByteArray(16))))) }
                advance(4_999)
                p.a.transport.connectAttempts.size shouldBe 1
                advance(1)
                p.a.transport.connectAttempts.size shouldBe 2
                advance(5_000)
                r.await() shouldBe OpenResult.Failed(OpenFailure.Timeout)
                p.a.transport.connectAttempts.map { it.target.ip } shouldBe listOf(ifB.address, Ipv4.of(192, 168, 1, 9))

                p.a.transport.connectBehavior = { null }
                val inbound = async { p.b.manager.pendingInbound.first() }
                val ok = p.a.manager.open(Target.Qr(QrPayload(p.b.id, "B", ips, p.b.port, fp, Bytes16.of(ByteArray(16)))))
                ok.shouldBeInstanceOf<OpenResult.Pairing>().connection.intent shouldBe Intent.QR_PAIR
                inbound.await().intent shouldBe Intent.QR_PAIR
            }
        }

        test("全部候选都不在本机子网内：NotLocal，不发起连接") {
            runTest {
                val p = Pair2(this)
                val qr = QrPayload(p.b.id, "B", listOf(Ipv4.of(8, 8, 8, 8).bits), p.b.port, Bytes.of(ByteArray(32)), Bytes16.of(ByteArray(16)))
                p.a.manager.open(Target.Qr(qr)) shouldBe OpenResult.Failed(OpenFailure.NotLocal)
                p.a.transport.connectAttempts.shouldBeEmpty()
            }
        }
    }

    context("监听端口（5.7、5.9）") {
        test("42580 → 42590–42599 → 系统分配；全部失败时报 Failed") {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val d1 = TestDevice("D1", 1, ifA, net, clock, backgroundScope)
                d1.transport.blockedTcpPorts += 42580
                d1.manager.startListening() shouldBe ListenResult.Ok(42590)
                val d2 = TestDevice("D2", 2, ifB, net, clock, backgroundScope)
                d2.transport.blockedTcpPorts += listOf(42580) + (42590..42599)
                val sys = d2.manager.startListening().shouldBeInstanceOf<ListenResult.Ok>().port
                (sys >= 50_000) shouldBe true
                val d3 = TestDevice("D3", 3, iface("wlan0", 192, 168, 1, 4), net, clock, backgroundScope)
                d3.transport.blockedTcpPorts += listOf(42580) + (42590..42599) + (50_000..50_010)
                d3.manager.startListening() shouldBe ListenResult.Failed
            }
        }
    }

    context("断开检测（18.8）") {
        test("RST：两端都立即判定为“连接被对方关闭”并注销") {
            runTest {
                val p = Pair2(this)
                p.mutualTrust()
                val s = p.a.manager.open(Target.Device(p.b.id)).shouldBeInstanceOf<OpenResult.Ok>().session
                runCurrent()
                val sb = p.b.manager.session(p.a.id)!!
                p.net.connections.single().reset()
                runCurrent()
                s.closed.await() shouldBe CloseReason.PeerClosed(null)
                sb.closed.await() shouldBe CloseReason.PeerClosed(null)
                testScheduler.currentTime shouldBe 0L
                p.a.manager.sessions.value shouldBe emptyMap()
            }
        }

        test("本地接口消失：本机以 LocalNetworkLost 关闭，对方读到 EOF") {
            runTest {
                val p = Pair2(this)
                p.mutualTrust()
                val s = p.a.manager.open(Target.Device(p.b.id)).shouldBeInstanceOf<OpenResult.Ok>().session
                runCurrent()
                val sb = p.b.manager.session(p.a.id)!!
                p.a.transport.monitor.set(emptySet())
                runCurrent()
                s.closed.await() shouldBe CloseReason.LocalNetworkLost
                sb.closed.await() shouldBe CloseReason.PeerClosed(null)
                testScheduler.currentTime shouldBe 0L
            }
        }
    }
})
