package com.lantransfer.domain

import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.AnnouncementCodec
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.testing.Gens
import com.lantransfer.testing.InterfaceSpec
import com.lantransfer.testing.InvalidAnnouncement
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.announcement
import com.lantransfer.testing.deviceId
import com.lantransfer.testing.interfaceSet
import com.lantransfer.testing.invalidAnnouncement
import com.lantransfer.testing.ipv4
import com.lantransfer.testing.ipv4InSubnet
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainAll
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.shouldBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.flatMap
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll

private fun toIfaces(specs: List<InterfaceSpec>): List<LocalInterface> =
    specs.map { LocalInterface(it.name, IfaceKind.valueOf(it.kind), Ipv4(it.address), it.prefixLength) }

private fun inSubnet(t: Ipv4, i: LocalInterface): Boolean = (t.bits xor i.address.bits) and subnetMask(i.prefixLength) == 0

private fun refAccepts(src: Ipv4, via: LocalInterface?, ifaces: List<LocalInterface>): Boolean =
    AddressRules.isPrivate(src) && if (via != null) inSubnet(src, via) else ifaces.any { inSubnet(src, it) }

/** 来源地址：偏向各接口子网内的地址。 */
private fun sourceArb(specs: List<InterfaceSpec>): Arb<Ipv4> =
    (if (specs.isEmpty()) Gens.ipv4() else Arb.choose(1 to Gens.ipv4(), 3 to Arb.element(specs).flatMap { Gens.ipv4InSubnet(it.address, it.prefixLength) })).map(::Ipv4)

private val dtArb: Arb<Long> = Arb.choose(
    3 to Arb.long(0L..2_000L),
    1 to Arb.long(9_000L..11_000L),
    1 to Arb.element(0L, 1_000L, 10_000L),
)

private sealed interface ListOp {
    data class Announce(val idIndex: Int, val a: Announcement, val src: Ipv4, val via: LocalInterface?, val dt: Long) : ListOp

    data class Tick(val dt: Long) : ListOp

    data class Trust(val mask: Int) : ListOp
}

private data class ListCase(val ifaces: List<LocalInterface>, val ids: List<DeviceId>, val ops: List<ListOp>)

/** 设备 ID 取自 4 个的小池子（第 0 个是本机），使同一 ID 的公告反复出现、字段不断变化。 */
private val listCase: Arb<ListCase> = Gens.interfaceSet().flatMap { specs ->
    val ifaces = toIfaces(specs)
    val via = Arb.element(listOf<LocalInterface?>(null) + ifaces)
    val op = Arb.choose(
        5 to Arb.bind(Arb.int(0..3), Gens.announcement(), sourceArb(specs), via, dtArb) { i, a, s, v, dt -> ListOp.Announce(i, a, s, v, dt) },
        3 to dtArb.map { ListOp.Tick(it) },
        1 to Arb.int(0..15).map { ListOp.Trust(it) },
    )
    Arb.bind(Arb.list(Gens.deviceId(), 4..4), Arb.list(op, 1..60)) { ids, ops -> ListCase(ifaces, ids, ops) }
}

private data class DropCase(val ifaces: List<LocalInterface>, val state: DeviceListState, val bad: InvalidAnnouncement, val src: Ipv4, val via: LocalInterface?, val now: Long)

/** 已有若干项的设备列表 + 一个非法数据报（一半取自列表中某项的公告，同一设备 ID）。 */
private val dropCase: Arb<DropCase> = Gens.interfaceSet().flatMap { specs ->
    val ifaces = toIfaces(specs)
    Arb.bind(Gens.deviceId(), Arb.list(Arb.bind(Gens.announcement(), sourceArb(specs), Arb.long(0L..20_000L)) { a, s, t -> Triple(a, s, t) }, 0..5)) { self, items ->
        DeviceListState(self, ifaces.toSet(), emptySet(), items.distinctBy { it.first.deviceId }.map { (a, s, t) -> DeviceEntry(a, s, t, false) })
    }.flatMap { state ->
        val base = if (state.entries.isEmpty()) Gens.announcement() else Arb.choice(Gens.announcement(), Arb.element(state.entries.map { it.announcement }))
        Arb.bind(Gens.invalidAnnouncement(base), sourceArb(specs), Arb.element(listOf<LocalInterface?>(null) + ifaces), Arb.long(20_000L..40_000L)) { bad, src, via, now ->
            DropCase(ifaces, state, bad, src, via, now)
        }
    }
}

class DeviceListPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 29: 设备列表归约
    // Validates: Requirements 5.2, 5.3, 5.4, 5.5, 5.8
    test("Property 29") {
        checkAll(Pbt.iterations(1_000), listCase) { (ifaces, ids, ops) ->
            val self = ids[0]
            var s = DeviceListState(self, ifaces.toSet())
            val model = mutableListOf<DeviceEntry>()
            var trusted = emptySet<DeviceId>()
            var now = 0L
            for (op in ops) {
                when (op) {
                    is ListOp.Announce -> {
                        now += op.dt
                        val a = op.a.copy(deviceId = ids[op.idIndex])
                        val before = s
                        s = DeviceListReducer.reduce(s, DeviceListEvent.Announced(a, op.src, op.via, now))
                        if (a.deviceId == self || !refAccepts(op.src, op.via, ifaces)) {
                            s shouldBe before
                        } else {
                            val e = DeviceEntry(a, op.src, now, a.deviceId in trusted)
                            val i = model.indexOfFirst { it.deviceId == a.deviceId }
                            if (i < 0) model += e else model[i] = e
                        }
                    }
                    is ListOp.Tick -> {
                        now += op.dt
                        s = DeviceListReducer.reduce(s, DeviceListEvent.Tick(now))
                        model.removeAll { it.lastSeenMono < now - 10_000 }
                        s.entries.all { it.lastSeenMono >= now - 10_000 } shouldBe true
                    }
                    is ListOp.Trust -> {
                        trusted = ids.filterIndexed { k, _ -> (op.mask shr k) and 1 == 1 }.toSet()
                        s = DeviceListReducer.reduce(s, DeviceListEvent.TrustChanged(trusted))
                        model.replaceAll { it.copy(trusted = it.deviceId in trusted) }
                    }
                }
                s.entries shouldBe model
                s.entries.map { it.deviceId }.toSet().size shouldBe s.entries.size
            }
        }
    }

    // Feature: lan-file-transfer, Property 9: 非法公告被丢弃
    // Validates: Requirements 23.2
    test("Property 9") {
        val seen = mutableSetOf<String>()
        checkAll(Pbt.iterations(1_000), dropCase) { c ->
            seen += c.bad.kind
            AnnouncementCodec.decode(c.bad.bytes).shouldBeNull()
            val after = DeviceListReducer.onDatagram(c.state, c.bad.bytes, c.src, c.via, c.now)
            after shouldBe c.state
            // 过期计时不变：之后的 Tick 与未收到该数据报时结果相同
            DeviceListReducer.reduce(after, DeviceListEvent.Tick(c.now)) shouldBe DeviceListReducer.reduce(c.state, DeviceListEvent.Tick(c.now))
        }
        seen shouldContainAll InvalidAnnouncement.kinds
    }

    test("示例：发现管线接受有效公告、忽略本机与非本地来源") {
        val wifi = LocalInterface("wlan0", IfaceKind.WIFI, Ipv4.of(192, 168, 1, 5), 24)
        val self = DeviceId(Bytes16.ofHex("00".repeat(16)))
        val peer = Announcement(DeviceId(Bytes16.ofHex("11".repeat(16))), "Pixel 9", DeviceType.ANDROID, ProtocolVersion.V1_0, 42580)
        var s = DeviceListState(self, setOf(wifi))
        s = DeviceListReducer.onDatagram(s, AnnouncementCodec.encode(peer), Ipv4.of(192, 168, 1, 9), wifi, 1_000)
        s.entries shouldBe listOf(DeviceEntry(peer, Ipv4.of(192, 168, 1, 9), 1_000, false))
        DeviceListReducer.onDatagram(s, AnnouncementCodec.encode(peer.copy(deviceId = self)), Ipv4.of(192, 168, 1, 9), wifi, 2_000) shouldBe s
        DeviceListReducer.onDatagram(s, AnnouncementCodec.encode(peer), Ipv4.of(10, 0, 0, 9), wifi, 2_000) shouldBe s
        val renamed = peer.copy(displayName = "小明的手机", port = 42590)
        s = DeviceListReducer.onDatagram(s, AnnouncementCodec.encode(renamed), Ipv4.of(192, 168, 1, 10), wifi, 5_000)
        s.entries shouldBe listOf(DeviceEntry(renamed, Ipv4.of(192, 168, 1, 10), 5_000, false))
        DeviceListReducer.reduce(s, DeviceListEvent.Tick(15_000)).entries.size shouldBe 1
        DeviceListReducer.reduce(s, DeviceListEvent.Tick(15_001)).entries.size shouldBe 0
        DeviceListReducer.reduce(s, DeviceListEvent.TrustChanged(setOf(peer.deviceId))).entries.single().trusted shouldBe true
    }
})
