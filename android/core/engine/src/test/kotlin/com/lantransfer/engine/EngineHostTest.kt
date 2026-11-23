@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.testing.FakeAppLifecycle
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryDatabase
import com.lantransfer.engine.testing.InMemoryHistoryStore
import com.lantransfer.engine.testing.InMemoryIdentityStore
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.InMemoryOutgoingTextCache
import com.lantransfer.engine.testing.InMemoryReceiveStorage
import com.lantransfer.engine.testing.InMemorySettingsStore
import com.lantransfer.engine.testing.InMemorySourceFiles
import com.lantransfer.engine.testing.InMemoryTaskStore
import com.lantransfer.engine.testing.InMemoryTrustStore
import com.lantransfer.engine.testing.RecordingClipboard
import com.lantransfer.engine.testing.RecordingNotifier
import com.lantransfer.engine.testing.SeededRng
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.TransferId
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.collections.shouldContain
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.random.Random

private val wlanA = iface("wlan0", 192, 168, 1, 2)
private val wlanB = iface("wlan0", 192, 168, 1, 3)

/** 一台由 EngineHost 组装的测试设备（全部端口都是内存实现）。 */
class HostDevice(
    val name: String,
    seed: Long,
    ifaces: Set<LocalInterface>,
    net: InMemoryNetwork,
    val clock: FakeClock,
    scope: CoroutineScope,
    type: DeviceType = DeviceType.ANDROID,
    profile: FsProfile = FsProfile.ANDROID,
    val identityStore: InMemoryIdentityStore = InMemoryIdentityStore(SeededRng(seed), name),
    val trust: InMemoryTrustStore = InMemoryTrustStore(),
) {
    val transport = net.host(name, ifaces)
    val db = InMemoryDatabase { clock.wallMs() }
    val history = InMemoryHistoryStore(db)
    val tasks = InMemoryTaskStore(db)
    val settings = InMemorySettingsStore()
    val storage = InMemoryReceiveStorage(profile, clock)
    val sources = InMemorySourceFiles()
    val cache = InMemoryOutgoingTextCache(sources)
    val clipboard = RecordingClipboard()
    val notifier = RecordingNotifier()
    val lifecycle = FakeAppLifecycle()
    val host = EngineHost(
        EnginePorts(
            transport, transport.monitor, identityStore, trust, history, tasks, settings, storage, sources, cache, clipboard, notifier, lifecycle,
            clock, SeededRng(seed + 1_000),
        ),
        type, profile, scope,
    )

    val services: EngineServices get() = host.services.value.shouldNotBeNull()
    val identity: DeviceIdentity get() = services.identity
    val id: DeviceId get() = identity.deviceId

    fun record(type: DeviceType = DeviceType.ANDROID) = TrustedDevice(id, name, type, identity.keyPair.publicKey, pairedAtUtcMs = 1)

    /** 双方互相记为可信设备（不记录地址：连接时只能从设备列表取地址）。 */
    fun trustsEachOther(other: HostDevice) {
        trust.upsert(other.record())
        other.trust.upsert(record())
    }

    fun file(name: String, size: Int): SourceRef {
        val ref = SourceRef("src:${this.name}/$name")
        sources.put(ref, name, Bytes.of(Random(name.hashCode()).nextBytes(size)))
        return ref
    }

    fun datagrams(net: InMemoryNetwork) = net.datagrams.filter { it.from == name }

    suspend fun awaitTask(id: TransferId, s: TaskState) = services.files.tasks.first { l -> l.any { it.id == id && it.task.state == s } }
}

private class HostWorld(scope: TestScope) {
    val clock = FakeClock(scope.testScheduler)
    val net = InMemoryNetwork()
    val a = HostDevice("A", 1, setOf(wlanA), net, clock, scope.backgroundScope)
    val b = HostDevice("B", 2, setOf(wlanB), net, clock, scope.backgroundScope, type = DeviceType.WINDOWS, profile = FsProfile.WINDOWS)
}

private fun sent(r: SendOfferResult): TransferId = r.shouldBeInstanceOf<SendOfferResult.Sent>().id

class EngineHostTest : FunSpec({
    context("启动与身份（4.1、4.6、5.9、Q14）") {
        test("首次启动：创建身份，先监听 42580 再发公告；重复调用返回同一结果") {
            runTest {
                val w = HostWorld(this)
                w.a.host.startNetworking() shouldBe StartResult.Running(42580)
                w.a.host.identity.value.shouldBeInstanceOf<IdentityLoad.Created>()
                w.a.transport.listeningPorts shouldContain 42580
                runCurrent()
                val ann = w.a.datagrams(w.net).first()
                com.lantransfer.protocol.AnnouncementCodec.decode(ann.data.toByteArray()).shouldNotBeNull().port shouldBe 42580
                w.a.services.discovery.status.value shouldBe DiscoveryStatus.Running
                w.a.host.startNetworking() shouldBe StartResult.Running(42580)
                w.a.identityStore.loads shouldBe 1
            }
        }

        test("身份损坏：生成新身份并报告 ResetCorrupted，保留可信设备列表，照常联网") {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val old = DeviceIdentity(deviceIdOf(7), keyPairOf(7), "A")
                val trust = InMemoryTrustStore().also { it.upsert(TrustedDevice(deviceIdOf(9), "旧设备", DeviceType.WINDOWS, Bytes.of(ByteArray(32) { 1 }), pairedAtUtcMs = 1)) }
                val a = HostDevice("A", 1, setOf(wlanA), net, clock, backgroundScope, identityStore = InMemoryIdentityStore(SeededRng(1), "A", old, corrupted = true), trust = trust)
                a.host.startNetworking() shouldBe StartResult.Running(42580)
                val reset = a.host.identity.value.shouldBeInstanceOf<IdentityLoad.ResetCorrupted>()
                (reset.identity.deviceId == old.deviceId) shouldBe false
                a.id shouldBe reset.identity.deviceId
                a.trust.get(deviceIdOf(9)).shouldNotBeNull()
            }
        }

        test("新身份无法保存：不监听、不发公告并报告；重试成功后启动") {
            runTest {
                val w = HostWorld(this)
                w.a.identityStore.failSaves = true
                w.a.host.startNetworking() shouldBe StartResult.IdentityNotSaved("injected failure")
                w.a.host.identity.value.shouldBeInstanceOf<IdentityLoad.SaveFailed>()
                w.a.host.services.value.shouldBeNull()
                w.a.host.networking.value shouldBe StartResult.IdentityNotSaved("injected failure")
                moveTimeBy(5_000)
                w.a.transport.listeningPorts.shouldBeEmpty()
                w.a.datagrams(w.net).shouldBeEmpty()
                w.a.identityStore.failSaves = false
                w.a.host.startNetworking() shouldBe StartResult.Running(42580)
                w.a.host.identity.value.shouldBeInstanceOf<IdentityLoad.Created>()
            }
        }

        test("全部端口都无法监听：不发公告并报告 ListenFailed；恢复后重试即可启动（5.9）") {
            runTest {
                val w = HostWorld(this)
                w.a.transport.listenFails = true
                w.a.host.startNetworking() shouldBe StartResult.ListenFailed
                moveTimeBy(5_000)
                w.a.datagrams(w.net).shouldBeEmpty()
                w.a.services.discovery.status.value shouldBe DiscoveryStatus.Stopped
                w.a.transport.listenFails = false
                w.a.host.startNetworking() shouldBe StartResult.Running(42580)
                runCurrent()
                (w.a.datagrams(w.net).isNotEmpty()) shouldBe true
            }
        }
    }

    context("组装与接线") {
        test("经发现得到对方地址：B 向 A 发送文字与文件，分发器交给 TextService 与 FileTransferEngine（3.2、5.2）") {
            runTest {
                val w = HostWorld(this)
                w.a.host.startNetworking()
                w.b.host.startNetworking()
                w.a.trustsEachOther(w.b)
                // A 的第一轮公告早于 B 打开 UDP 端口；B 在 A 的下一轮（5 s 内）看到 A
                val t0 = testScheduler.currentTime
                w.b.services.discovery.devices.first { l -> l.any { it.deviceId == w.a.id } }
                (testScheduler.currentTime - t0 <= 5_000) shouldBe true
                w.b.services.text.send(w.a.id, "你好，来自 Windows").shouldBeInstanceOf<SendCheck.Sent>()
                w.a.services.text.conversation(w.b.id).first { it.isNotEmpty() }.single().text shouldBe "你好，来自 Windows"
                w.a.trust.setAutoAccept(w.b.id, true)
                val ref = w.b.file("报告.pdf", 300_000)
                val id = sent(w.b.services.files.send(w.a.id, listOf(ref)))
                w.a.awaitTask(id, TaskState.COMPLETED)
                w.a.storage.filesIn(DirRef.Default)["报告.pdf"] shouldBe w.b.sources.entry(ref)!!.data
            }
        }

        test("解除配对：取消该对端的传输（对方 PEER_CANCELLED）并关闭 Session（Q7、9.3）") {
            runTest {
                val w = HostWorld(this)
                w.a.host.startNetworking()
                w.b.host.startNetworking()
                w.a.trustsEachOther(w.b)
                w.b.trust.setAutoAccept(w.a.id, true)
                runCurrent()
                val id = sent(w.a.services.files.send(w.b.id, listOf(w.a.file("big", 8_000_000))))
                w.b.services.files.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                w.a.services.pairing.unpair(w.b.id)
                w.a.services.files.tasks.value.single { it.id == id }.task.state shouldBe TaskState.CANCELLED
                w.b.awaitTask(id, TaskState.PEER_CANCELLED)
                w.a.trust.get(w.b.id).shouldBeNull()
                runCurrent()
                w.a.services.sessions.session(w.b.id).shouldBeNull()
            }
        }

        test("shutdownNetworking 关闭全部 Session、停止发现与监听；再次 startNetworking 恢复，不重复载入身份（21.4、21.9）") {
            runTest {
                val w = HostWorld(this)
                w.a.host.startNetworking()
                w.b.host.startNetworking()
                w.a.trustsEachOther(w.b)
                runCurrent()
                w.a.services.text.send(w.b.id, "hi")
                w.b.services.text.conversation(w.a.id).first { it.isNotEmpty() }
                w.a.services.sessions.session(w.b.id).shouldNotBeNull()
                w.a.host.shutdownNetworking(SessionCloseReason.BACKGROUND)
                w.a.host.networking.value.shouldBeNull()
                w.a.services.sessions.sessions.value.isEmpty() shouldBe true
                w.a.transport.listeningPorts.shouldBeEmpty()
                w.a.services.discovery.status.value shouldBe DiscoveryStatus.Stopped
                runCurrent()
                w.b.services.sessions.session(w.a.id).shouldBeNull()
                val n = w.a.datagrams(w.net).size
                moveTimeBy(12_000)
                w.a.datagrams(w.net).size shouldBe n
                w.b.services.discovery.devices.value.shouldBeEmpty() // A 停止公告满 10 s 后从 B 的列表中移除
                w.a.host.startNetworking() shouldBe StartResult.Running(42580)
                runCurrent()
                (w.a.datagrams(w.net).size > n) shouldBe true
                w.a.identityStore.loads shouldBe 1
            }
        }
    }
})
