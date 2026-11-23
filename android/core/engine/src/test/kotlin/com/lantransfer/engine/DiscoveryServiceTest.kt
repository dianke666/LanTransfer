@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.IfaceKind
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.UdpOpenResult
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.InMemoryTrustStore
import com.lantransfer.engine.testing.SeededRng
import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.AnnouncementCodec
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolVersion
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.collections.shouldContainExactlyInAnyOrder
import io.kotest.matchers.collections.shouldHaveSize
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private val wlanA = iface("wlan0", 192, 168, 1, 2)
private val wlanB = iface("wlan0", 192, 168, 1, 3)
private val hotspotA = LocalInterface("ap0", IfaceKind.HOTSPOT, Ipv4.of(192, 168, 43, 1), 24)
private val ethA = LocalInterface("eth0", IfaceKind.ETHERNET, Ipv4.of(10, 0, 0, 2), 24)

/** 一台只带 DiscoveryService 的测试设备。 */
private class DiscDevice(name: String, idByte: Int, ifaces: Set<LocalInterface>, net: InMemoryNetwork, clock: FakeClock, scope: CoroutineScope) {
    val transport = net.host(name, ifaces)
    val trust = InMemoryTrustStore()
    val identity = DeviceIdentity(deviceIdOf(idByte), keyPairOf(idByte), name)
    val service = DiscoveryService(transport, transport.monitor, identity, DeviceType.ANDROID, trust, clock, SeededRng(idByte.toLong()), scope)
}

private class DiscWorld(scope: TestScope) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = DiscDevice("A", 1, setOf(wlanA), net, clock, scope.backgroundScope)
    val b = DiscDevice("B", 2, setOf(wlanB), net, clock, scope.backgroundScope)

    fun sentBy(name: String) = net.datagrams.filter { it.from == name }
}

class DiscoveryServiceTest : FunSpec({
    test("启动时立即发送；每个活动接口每 2 s（±0.2 s）向定向广播与组播组各发 1 份；新接口出现时立即在该接口上发送（5.1、3.6）") {
        runTest {
            val w = DiscWorld(this)
            w.a.transport.monitor.set(setOf(wlanA, hotspotA))
            w.a.service.start(42580)
            runCurrent()
            val first = w.sentBy("A")
            first.map { it.via to it.dst } shouldContainExactlyInAnyOrder listOf(
                wlanA to wlanA.broadcast, wlanA to DiscoveryService.MULTICAST, hotspotA to hotspotA.broadcast, hotspotA to DiscoveryService.MULTICAST,
            )
            first.forEach { it.port shouldBe ProtocolConstants.DISCOVERY_PORT }
            AnnouncementCodec.decode(first[0].data.toByteArray()) shouldBe
                Announcement(w.a.identity.deviceId, "A", DeviceType.ANDROID, ProtocolVersion.CURRENT, 42580)
            // 逐毫秒推进 30 s，记录每一轮的时刻
            val rounds = ArrayList<Long>()
            var seen = first.size
            val t0 = testScheduler.currentTime
            rounds += t0
            repeat(30_000) {
                moveTimeBy(1)
                val n = w.sentBy("A").size
                if (n != seen) {
                    (n - seen) shouldBe 4
                    seen = n
                    rounds += testScheduler.currentTime
                }
            }
            val gaps = rounds.zipWithNext { x, y -> y - x }
            gaps.all { it in 1_800..2_200 } shouldBe true
            (gaps.toSet().size > 1) shouldBe true // 有抖动
            // 新接口：立即只在该接口上发送
            w.a.transport.monitor.set(setOf(wlanA, hotspotA, ethA))
            runCurrent()
            val added = w.sentBy("A").drop(seen)
            added.map { it.via to it.dst } shouldContainExactlyInAnyOrder listOf(ethA to ethA.broadcast, ethA to DiscoveryService.MULTICAST)
        }
    }

    test("双方互相出现在列表中（不含自己）；最后一次公告满 10 s 时仍在，之后 2 s 内移除，信任信息保留（5.2、5.4）") {
        runTest {
            val w = DiscWorld(this)
            w.a.trust.upsert(TrustedDevice(w.b.identity.deviceId, "B", DeviceType.ANDROID, w.b.identity.keyPair.publicKey, pairedAtUtcMs = 1))
            w.a.service.start(42580)
            w.b.service.start(42590)
            runCurrent()
            val eb = w.a.service.devices.value.single()
            eb.deviceId shouldBe w.b.identity.deviceId
            eb.ip shouldBe wlanB.address
            eb.announcement.port shouldBe 42590
            eb.trusted shouldBe true
            w.a.service.addressOf(w.b.identity.deviceId) shouldBe com.lantransfer.engine.ports.SocketAddr(wlanB.address, 42590)
            w.b.service.devices.value.single().deviceId shouldBe w.a.identity.deviceId
            w.b.service.devices.value.single().trusted shouldBe false
            // B 停止：最后一次公告在 t0
            val t0 = testScheduler.currentTime
            w.b.service.stop()
            w.b.service.status.value shouldBe DiscoveryStatus.Stopped
            w.b.service.devices.value.shouldBeEmpty()
            moveTimeTo(t0 + 10_000)
            w.a.service.devices.value shouldHaveSize 1
            moveTimeTo(t0 + 12_000)
            w.a.service.devices.value.shouldBeEmpty()
            w.a.trust.get(w.b.identity.deviceId).shouldNotBeNull()
            // B 重新开始后 5 s 内再次出现
            w.b.service.start(42590)
            runCurrent()
            w.a.service.devices.value.single().deviceId shouldBe w.b.identity.deviceId
        }
    }

    test("信任变化更新列表中的信任标识；无效数据报不改变列表") {
        runTest {
            val w = DiscWorld(this)
            w.a.service.start(42580)
            w.b.service.start(42580)
            runCurrent()
            w.a.service.devices.value.single().trusted shouldBe false
            w.a.trust.upsert(TrustedDevice(w.b.identity.deviceId, "B", DeviceType.ANDROID, w.b.identity.keyPair.publicKey, pairedAtUtcMs = 1))
            runCurrent()
            w.a.service.devices.value.single().trusted shouldBe true
            val before = w.a.service.devices.value
            val junk = w.net.host("J", setOf(iface("wlan0", 192, 168, 1, 9))).openUdp(50_123).shouldBeInstanceOf<UdpOpenResult.Ok>().endpoint
            junk.send(iface("wlan0", 192, 168, 1, 9), Ipv4.of(192, 168, 1, 255), ProtocolConstants.DISCOVERY_PORT, byteArrayOf(1, 2, 3))
            runCurrent()
            w.a.service.devices.value shouldBe before
        }
    }

    test("UDP 端口被占用或全部发送失败时为 Unavailable，发送恢复后回到 Running；没有接口时不发送（5.10）") {
        runTest {
            val w = DiscWorld(this)
            w.a.transport.blockedUdpPorts += ProtocolConstants.DISCOVERY_PORT
            w.a.service.start(42580)
            w.a.service.status.value.shouldBeInstanceOf<DiscoveryStatus.Unavailable>()
            runCurrent()
            w.sentBy("A").shouldBeEmpty()

            w.b.transport.udpSendFails = true
            w.b.service.start(42580)
            runCurrent()
            w.b.service.status.value shouldBe DiscoveryStatus.Unavailable(DiscoveryService.SEND_FAILED)
            w.b.transport.udpSendFails = false
            moveTimeBy(2_200)
            w.b.service.status.value shouldBe DiscoveryStatus.Running

            w.b.service.stop()
            w.b.transport.monitor.set(emptySet())
            val n = w.sentBy("B").size
            w.b.service.start(42580)
            moveTimeBy(10_000)
            w.b.service.status.value shouldBe DiscoveryStatus.Running
            w.sentBy("B") shouldHaveSize n
        }
    }
})
