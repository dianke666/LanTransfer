@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine.testing

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.DirStatus
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.IfaceKind
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.SourceStat
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TransferTask
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.ClipRead
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.engine.ports.CommitResult
import com.lantransfer.engine.ports.ConnectResult
import com.lantransfer.engine.ports.FileRecord
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.ports.IoResult
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.ReadResult
import com.lantransfer.engine.ports.RecordId
import com.lantransfer.engine.ports.RequestKind
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.ports.UdpEndpoint
import com.lantransfer.engine.ports.UdpOpenResult
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import com.lantransfer.testing.Gens
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.collections.shouldContainExactly
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.next
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.random.Random

private fun iface(name: String, a: Int, b: Int, c: Int, d: Int, prefix: Int = 24) = LocalInterface(name, IfaceKind.WIFI, Ipv4.of(a, b, c, d), prefix)

private fun id16(b: Int) = Bytes16.of(ByteArray(16) { b.toByte() })

private suspend fun TcpConn.readAll(n: Int): ByteArray {
    val out = ByteArray(n)
    var got = 0
    while (got < n) {
        val k = read(out, got, n - got)
        if (k < 0) break
        got += k
    }
    return out.copyOf(got)
}

private fun UdpOpenResult.endpoint(): UdpEndpoint = (this as UdpOpenResult.Ok).endpoint

class EngineFixturesTest : FunSpec({
    val ifA = iface("wlan0", 192, 168, 1, 2)
    val ifB = iface("eth0", 192, 168, 1, 3)
    val ifC = iface("eth0", 10, 0, 0, 5, 8)

    context("InMemoryTransport：TCP") {
        test("成对连接：双向传输、捕获线上字节、关闭后读到 EOF") {
            runTest {
                val net = InMemoryNetwork()
                val a = net.host("A", setOf(ifA))
                val b = net.host("B", setOf(ifB))
                val listener = b.listen(listOf(42580))!!
                listener.port shouldBe 42580
                val accepted = async { listener.accept()!! }
                val client = (a.connect(ifA, ifB.address, 42580, 10_000) as ConnectResult.Ok).conn
                val server = accepted.await()
                client.remote shouldBe SocketAddr(ifB.address, 42580)
                server.remote shouldBe client.local
                client.write("hello".encodeToByteArray()) shouldBe true
                server.readAll(5).decodeToString() shouldBe "hello"
                server.write(byteArrayOf(1, 2, 3)) shouldBe true
                client.readAll(3).toList() shouldBe listOf<Byte>(1, 2, 3)
                net.connections.single().bytesFromClient().decodeToString() shouldBe "hello"
                net.connections.single().bytesFromServer().toList() shouldBe listOf<Byte>(1, 2, 3)

                client.write("bye".encodeToByteArray()) shouldBe true
                client.close()
                server.readAll(3).decodeToString() shouldBe "bye" // 先收完已发出的数据
                server.read(ByteArray(4)) shouldBe -1
                server.write(byteArrayOf(9)) shouldBe false
                client.write(byteArrayOf(9)) shouldBe false
            }
        }

        test("连接失败：拒绝、不可达、接口不在本机、注入的超时与绑定失败；每次调用都被记录") {
            runTest {
                val net = InMemoryNetwork()
                val a = net.host("A", setOf(ifA))
                net.host("C", setOf(ifC))
                a.connect(ifA, ifB.address, 42580, 10_000) shouldBe ConnectResult.Unreachable // 没有主机
                net.host("B", setOf(ifB))
                a.connect(ifA, ifB.address, 42580, 10_000) shouldBe ConnectResult.Refused // 没有监听
                a.connect(ifA, ifC.address, 42580, 10_000) shouldBe ConnectResult.Unreachable // 不在发送接口的子网内
                a.connect(ifB, ifB.address, 42580, 10_000) shouldBe ConnectResult.BindFailed(vpnSuspected = false)
                a.connectBehavior = { if (it.port == 1) ConnectBehavior.Hang else ConnectBehavior.BindFail(vpnSuspected = true) }
                val start = testScheduler.currentTime
                a.connect(ifA, ifB.address, 1, 5_000) shouldBe ConnectResult.Timeout
                (testScheduler.currentTime - start) shouldBe 5_000L
                a.connect(ifA, ifB.address, 2, 5_000) shouldBe ConnectResult.BindFailed(vpnSuspected = true)
                a.connectAttempts.map { it.target.port } shouldContainExactly listOf(42580, 42580, 42580, 42580, 1, 2)
            }
        }

        test("监听端口回退与系统分配；关闭后 accept 返回 null") {
            runTest {
                val net = InMemoryNetwork()
                val b = net.host("B", setOf(ifB))
                b.blockedTcpPorts += 42580
                val l1 = b.listen(listOf(42580, 42590))!!
                l1.port shouldBe 42590
                b.listen(listOf(42590)).shouldBeNull() // 已被自己占用
                val l2 = b.listen(listOf(0))!!
                (l2.port >= 50_000) shouldBe true
                b.listeningPorts shouldBe setOf(42590, l2.port)
                l1.close()
                l1.accept().shouldBeNull()
                b.listeningPorts shouldBe setOf(l2.port)
            }
        }

        test("RST 让双方立即读到 -1；静默断网后数据丢失且收不到 EOF") {
            runTest {
                val net = InMemoryNetwork()
                val a = net.host("A", setOf(ifA))
                val b = net.host("B", setOf(ifB))
                val listener = b.listen(listOf(42580))!!
                val c1 = (a.connect(ifA, ifB.address, 42580, 1_000) as ConnectResult.Ok).conn
                val s1 = listener.accept()!!
                c1.write(byteArrayOf(1)) shouldBe true
                net.connections[0].reset()
                s1.read(ByteArray(4)) shouldBe -1
                c1.read(ByteArray(4)) shouldBe -1
                c1.write(byteArrayOf(2)) shouldBe false

                val c2 = (a.connect(ifA, ifB.address, 42580, 1_000) as ConnectResult.Ok).conn
                val s2 = listener.accept()!!
                net.connections[1].blackhole()
                c2.write(byteArrayOf(7)) shouldBe true
                withTimeoutOrNull(20_000) { s2.read(ByteArray(4)) }.shouldBeNull()
                net.connections[1].bytesFromClient().toList() shouldBe listOf<Byte>(7)
            }
        }
    }

    context("InMemoryTransport：UDP") {
        test("定向广播与组播只投递到同一子网（含回环），单播只投递到目标；Android 不报告接收接口") {
            runTest {
                val net = InMemoryNetwork()
                val a = net.host("A", setOf(ifA))
                val b = net.host("B", setOf(ifB), reportUdpVia = false)
                val c = net.host("C", setOf(ifC))
                val ea = a.openUdp(42581).endpoint()
                val eb = b.openUdp(42581).endpoint()
                val ec = c.openUdp(42581).endpoint()
                ea.send(ifA, ifA.broadcast, 42581, byteArrayOf(1)) shouldBe true
                val atB = eb.receive()!!
                atB.data shouldBe Bytes.of(byteArrayOf(1))
                atB.src shouldBe ifA.address
                atB.srcPort shouldBe 42581
                atB.via.shouldBeNull()
                ea.receive()!!.via shouldBe ifA // 回环
                ea.send(ifA, InMemoryNetwork.MULTICAST_GROUP, 42581, byteArrayOf(2)) shouldBe true
                eb.receive()!!.data shouldBe Bytes.of(byteArrayOf(2))
                ea.send(ifA, ifB.address, 42581, byteArrayOf(3)) shouldBe true
                eb.receive()!!.data shouldBe Bytes.of(byteArrayOf(3))
                withTimeoutOrNull(1_000) { ec.receive() }.shouldBeNull()
                net.datagrams.map { it.dst } shouldContainExactly listOf(ifA.broadcast, InMemoryNetwork.MULTICAST_GROUP, ifB.address)
            }
        }

        test("端口被占用时打开失败；注入发送失败；关闭后 receive 返回 null") {
            runTest {
                val net = InMemoryNetwork()
                val a = net.host("A", setOf(ifA))
                a.blockedUdpPorts += 42581
                a.openUdp(42581).shouldBeInstanceOf<UdpOpenResult.Failed>()
                val e = a.openUdp(42582).endpoint()
                a.openUdp(42582).shouldBeInstanceOf<UdpOpenResult.Failed>()
                a.udpSendFails = true
                e.send(ifA, ifA.broadcast, 42582, byteArrayOf(1)) shouldBe false
                e.close()
                e.receive().shouldBeNull()
            }
        }

        test("FakeNetworkMonitor 改变接口集合并记录 setFastPolling") {
            val m = FakeNetworkMonitor(setOf(ifA))
            m.set(setOf(ifA, ifB))
            m.interfaces.value shouldBe setOf(ifA, ifB)
            m.setFastPolling(true)
            m.setFastPolling(false)
            m.fastPollingHistory shouldContainExactly listOf(true, false)
            m.fastPolling shouldBe false
        }
    }

    context("FakeClock") {
        test("与 runTest 的虚拟时间一致；手动推进；系统时间偏移不影响单调时间") {
            runTest {
                val clock = FakeClock(testScheduler)
                clock.monotonicMs() shouldBe 0L
                delay(1_500)
                clock.monotonicMs() shouldBe 1_500L
                clock.wallMs() shouldBe FakeClock.DEFAULT_WALL_START_MS + 1_500
                clock.advanceBy(500)
                clock.monotonicMs() shouldBe 2_000L
                clock.wallSkewMs = -3_600_000
                clock.monotonicMs() shouldBe 2_000L
                clock.wallMs() shouldBe FakeClock.DEFAULT_WALL_START_MS + 2_000 - 3_600_000
            }
            val manual = FakeClock()
            manual.advanceBy(10)
            manual.monotonicMs() shouldBe 10L
            manual.localNow().year shouldBe 2025
        }
    }

    context("内存版存储") {
        val peer = DeviceId(id16(1))
        val tid = TransferId(id16(2))
        fun text(m: Int, utc: Long) = TextRecord(0, peer, "对方", MessageId(id16(m)), Direction.RECEIVE, Bytes.of("t$m".encodeToByteArray()), utc, RecordTime(utc, 480), TextStatus.RECEIVED)
        fun file(i: Int) = TransferFile(tid, i, 0, "f$i", 10, "text/plain", null, FileState.WAITING)
        fun task(state: TaskState, vararg idx: Int) =
            TransferTask(tid, peer, "对方", Direction.SEND, state, null, null, RecordTime(1_500, 480), 0, idx.map { file(it) })

        test("HistoryStore：去重、共用 seq、按（记录时间, seq）排序、删除文件记录、清空不影响 pending_cancel") {
            val db = InMemoryDatabase()
            val history = InMemoryHistoryStore(db)
            val tasks = InMemoryTaskStore(db)
            history.appendText(text(10, 2_000)) shouldBe StoreResult.Ok
            history.appendText(text(10, 9_999)) shouldBe StoreResult.Duplicate
            tasks.save(task(TaskState.RUNNING, 0, 1)) shouldBe StoreResult.Ok
            history.appendText(text(11, 1_500)) shouldBe StoreResult.Ok
            val listed = (history.list() as ListResult.Ok).items
            listed.map { it.seq } shouldContainExactly listOf(2L, 3L, 4L, 1L) // 1 500 ms 的两个文件、1 500 ms 的文字、2 000 ms 的文字
            (listed[0] as HistoryRecord.File).record.taskState shouldBe TaskState.RUNNING

            // 重新保存保留 seq；新增文件分配新 seq
            tasks.save(task(TaskState.INTERRUPTED, 0, 1, 2)) shouldBe StoreResult.Ok
            tasks.get(tid)!!.files.map { it.seq } shouldContainExactly listOf(2L, 3L, 5L)
            tasks.unfinished().size shouldBe 1

            history.updateTextStatus(MessageId(id16(10)), peer, TextStatus.DELIVERED) shouldBe StoreResult.Ok
            history.findText(peer, MessageId(id16(10)))!!.status shouldBe TextStatus.DELIVERED
            history.delete(RecordId(1)) shouldBe StoreResult.Ok
            history.findText(peer, MessageId(id16(10))).shouldBeNull()

            history.deleteFileRecord(tid, 0)
            history.deleteFileRecord(tid, 1)
            tasks.get(tid)!!.files.map { it.index } shouldContainExactly listOf(2)
            history.deleteFileRecord(tid, 2)
            tasks.get(tid).shouldBeNull() // 没有剩余文件行时任务行也被删除

            tasks.addPendingCancel(peer, tid) shouldBe StoreResult.Ok
            tasks.addPendingCancel(peer, tid) shouldBe StoreResult.Ok // 主键 (peer, transfer_id)
            history.upsertFile(FileRecord(file(0), peer, "对方", Direction.RECEIVE, RecordTime(3_000, 0), TaskState.COMPLETED)) shouldBe StoreResult.Ok
            history.clearAll() shouldBe StoreResult.Ok
            (history.list() as ListResult.Ok).items.shouldBeEmpty()
            history.updateTextStatus(MessageId(id16(11)), peer, TextStatus.FAILED) // 迟到的更新不重新创建记录
            (history.list() as ListResult.Ok).items.shouldBeEmpty()
            db.pendingCancels().size shouldBe 1
            tasks.takePendingCancels(peer) shouldContainExactly listOf(tid)
            tasks.takePendingCancels(peer).shouldBeEmpty()
            history.appendText(text(12, 1)) shouldBe StoreResult.Ok
            history.findText(peer, MessageId(id16(12)))!!.seq shouldBe 7L // 清空不重置计数器

            db.failWrites = { it == "appendText" }
            history.appendText(text(13, 1)).shouldBeInstanceOf<StoreResult.Failed>()
            history.findText(peer, MessageId(id16(13))).shouldBeNull()
            db.failReads = true
            history.list().shouldBeInstanceOf<ListResult.Failed>()
        }

        test("TrustStore：整条替换、删除、更新名称与地址，并发出变化通知") {
            runTest {
                val trust = InMemoryTrustStore()
                val d = TrustedDevice(peer, "Pixel", DeviceType.ANDROID, Bytes.of(ByteArray(32)), autoAccept = true, pairedAtUtcMs = 1)
                val changed = async { trust.changes.first() }
                testScheduler.runCurrent()
                trust.upsert(d) shouldBe StoreResult.Ok
                changed.await()
                trust.upsert(d.copy(displayName = "新名称")) shouldBe StoreResult.Ok
                trust.all() shouldContainExactly listOf(d.copy(displayName = "新名称"))
                trust.setAutoAccept(peer, false)
                trust.updateName(peer, "改名")
                trust.recordAddress(peer, Ipv4.of(192, 168, 1, 9), 42590)
                trust.get(peer) shouldBe d.copy(displayName = "改名", autoAccept = false, lastAddress = Ipv4.of(192, 168, 1, 9), lastPort = 42590)
                trust.delete(peer)
                trust.get(peer).shouldBeNull()
                trust.setAutoAccept(peer, true) shouldBe StoreResult.Ok // 不存在时不做任何事
                trust.all().shouldBeEmpty()
            }
        }

        test("IdentityStore：首次创建、再次载入、损坏后重置且保留显示名称") {
            val store = InMemoryIdentityStore(SeededRng(1), "Pixel 9")
            val created = store.loadOrCreate().shouldBeInstanceOf<IdentityLoad.Created>()
            store.loadOrCreate().shouldBeInstanceOf<IdentityLoad.Loaded>().identity.deviceId shouldBe created.identity.deviceId
            store.setDisplayName("小明的手机") shouldBe StoreResult.Ok
            store.loadOrCreate().shouldBeInstanceOf<IdentityLoad.Loaded>().identity.displayName shouldBe "小明的手机"
            val broken = InMemoryIdentityStore(SeededRng(2), "默认", created.identity, corrupted = true)
            val reset = broken.loadOrCreate().shouldBeInstanceOf<IdentityLoad.ResetCorrupted>()
            (reset.identity.deviceId == created.identity.deviceId) shouldBe false
            reset.identity.displayName shouldBe "Pixel 9"
            broken.loadOrCreate().shouldBeInstanceOf<IdentityLoad.Loaded>()
        }

        test("SettingsStore：当前值与变化") {
            val settings = InMemorySettingsStore()
            settings.update { it.copy(autoCopy = true) } shouldBe StoreResult.Ok
            settings.current().autoCopy shouldBe true
            settings.changes.value.autoCopy shouldBe true
            settings.failWrites = true
            settings.update { it.copy(autoCopy = false) }.shouldBeInstanceOf<StoreResult.Failed>()
            settings.current().autoCopy shouldBe true
        }
    }

    context("内存版文件") {
        val tid = TransferId(id16(3))
        val dir = DirRef.Path("D:\\接收")

        test("ReceiveStorage：目录状态、截断追加、空间不足、写入失败、重名提交（Windows 不区分大小写）") {
            val clock = FakeClock()
            val storage = InMemoryReceiveStorage(FsProfile.WINDOWS, clock)
            storage.probe(dir) shouldBe DirStatus.USABLE // 不存在但可创建
            storage.dir(DirRef.Default).creatable = false
            storage.probe(DirRef.Default) shouldBe DirStatus.CANNOT_CREATE
            storage.ensureDirectory(DirRef.Default) shouldBe false
            storage.ensureDirectory(dir) shouldBe true
            storage.putFile(dir, "A.txt", Bytes.of(byteArrayOf(0)))

            val temp = storage.createTemp(dir, tid, 0)
            storage.createTemp(dir, tid, 0) shouldBe temp
            storage.length(temp) shouldBe 0L
            val w = (storage.openAppend(temp, 0) as WriteResult.Ok).value
            w.write("hello".encodeToByteArray()) shouldBe IoResult.Ok
            w.close()
            clock.advanceBy(1_000)
            val w2 = (storage.openAppend(temp, 2) as WriteResult.Ok).value // 截断到 2 字节后追加
            w2.write("LLO".encodeToByteArray()) shouldBe IoResult.Ok
            w2.flushSync() shouldBe IoResult.Ok
            storage.temp(temp)!!.bytes().decodeToString() shouldBe "heLLO"
            storage.listTemps().single().lastWriteUtcMs shouldBe clock.wallMs()
            storage.filesIn(dir).keys shouldBe setOf("A.txt") // 临时文件不以最终文件名出现
            storage.openAppend(temp, 99).shouldBeInstanceOf<WriteResult.Failed>()

            val reader = (storage.openRead(temp) as ReadResult.Ok).value
            val buf = ByteArray(16)
            reader.read(buf) shouldBe ReadResult.Ok(5)
            reader.read(buf) shouldBe ReadResult.Ok(-1)

            val committed = storage.commit(temp, "a.txt").shouldBeInstanceOf<CommitResult.Ok>()
            committed.finalName shouldBe "a (1).txt"
            storage.filesIn(dir)["a (1).txt"] shouldBe Bytes.of("heLLO".encodeToByteArray())
            storage.length(temp).shouldBeNull()

            val t2 = storage.createTemp(dir, tid, 1)
            storage.dir(dir).freeSpace = 3
            val w3 = (storage.openAppend(t2, 0) as WriteResult.Ok).value
            w3.write(ByteArray(4)) shouldBe IoResult.Failed("存储空间不足")
            w3.write(ByteArray(3)) shouldBe IoResult.Ok
            storage.dir(dir).freeSpace shouldBe 0L
            storage.writeFailure = { _, at -> if (at >= 3) "磁盘错误" else null }
            storage.dir(dir).freeSpace = null
            w3.write(ByteArray(1)) shouldBe IoResult.Failed("磁盘错误")
            storage.commitFailure = { "重命名失败" }
            storage.commit(t2, "b.txt") shouldBe CommitResult.Failed("重命名失败")
            storage.delete(t2) shouldBe true
            storage.delete(t2) shouldBe false
        }

        test("Android 配置下重名区分大小写") {
            val storage = InMemoryReceiveStorage(FsProfile.ANDROID, FakeClock())
            storage.putFile(DirRef.Default, "A.txt", Bytes.EMPTY)
            val temp = storage.createTemp(DirRef.Default, tid, 0)
            (storage.commit(temp, "a.txt") as CommitResult.Ok).finalName shouldBe "a.txt"
            storage.exists(DirRef.Default, "a.TXT") shouldBe false
        }

        test("SourceFiles：元数据、从偏移读取、读到一半失败、文件夹与不存在的文件") {
            val files = InMemorySourceFiles()
            val ref = SourceRef("content://x/1")
            files.put(ref, "photo.jpg", Bytes.of(ByteArray(10) { it.toByte() }), mtime = 5, mime = "image/jpeg")
            files.describe(ref)!!.size shouldBe 10L
            files.stat(ref) shouldBe SourceStat(10, 5)
            val r = (files.open(ref, 4) as ReadResult.Ok).value
            val buf = ByteArray(100)
            r.read(buf) shouldBe ReadResult.Ok(6)
            buf[0] shouldBe 4.toByte()
            files.entry(ref)!!.failAt = 3
            val r2 = (files.open(ref, 0) as ReadResult.Ok).value
            r2.read(buf) shouldBe ReadResult.Ok(3)
            r2.read(buf).shouldBeInstanceOf<ReadResult.Failed>()
            files.putDirectory(SourceRef("content://x/dir"), "相册")
            files.describe(SourceRef("content://x/dir"))!!.isDirectory shouldBe true
            files.stat(SourceRef("content://x/dir")).shouldBeNull()
            files.open(SourceRef("content://x/none"), 0).shouldBeInstanceOf<ReadResult.Failed>()
            files.modify(ref, Bytes.of(ByteArray(2)), 6)
            files.stat(ref) shouldBe SourceStat(2, 6)
        }
    }

    context("系统服务替身与生成器") {
        test("剪贴板、通知与 FileLocator 记录调用；注入失败") {
            runTest {
                val clip = RecordingClipboard()
                clip.readText() shouldBe ClipRead.NoText
                clip.writeText("a\u0000b") shouldBe ClipWrite.Ok
                clip.readText() shouldBe ClipRead.Text("a\u0000b")
                clip.failWrites = true
                clip.writeText("x") shouldBe ClipWrite.Failed
                clip.writes shouldContainExactly listOf("a\u0000b", "x")
                clip.failReads = true
                clip.readText() shouldBe ClipRead.Failed
            }
            val notifier = RecordingNotifier()
            val key = NotifyKey.PairingRequest(DeviceId(id16(4)))
            notifier.request(key, "Pixel", RequestKind.PAIRING, null)
            notifier.active.keys shouldBe setOf(key)
            notifier.cancel(key)
            notifier.active.keys.shouldBeEmpty()
            notifier.events.size shouldBe 2
            val locator = RecordingFileLocator()
            locator.openDirectory(DirRef.Default)
            locator.calls shouldContainExactly listOf(LocateCall.OpenDirectory(DirRef.Default))
        }

        test("引擎生成器产生合法样本") {
            val rs = io.kotest.property.RandomSource.seeded(42)
            repeat(50) {
                val files = Gens.sourceFileSet().next(rs)
                (files.size in 1..5) shouldBe true
                files.all { it.name.isNotEmpty() && it.content.size <= 5 * 65_536 } shouldBe true
                val total = files.sumOf { it.content.size }.toLong()
                Gens.interruptionPoints(total).next(rs).all { it in 0..total } shouldBe true
            }
        }
    }
})
