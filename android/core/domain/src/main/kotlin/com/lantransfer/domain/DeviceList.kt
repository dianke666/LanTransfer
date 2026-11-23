package com.lantransfer.domain

import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.AnnouncementCodec
import com.lantransfer.protocol.DeviceId

/** 设备列表中的一项（需求 5.3）：显示名称、类型、最近一次有效公告的来源 IP、端口与信任标识。 */
data class DeviceEntry(
    val announcement: Announcement,
    val ip: Ipv4,
    val lastSeenMono: Long,
    val trusted: Boolean,
) {
    val deviceId: DeviceId get() = announcement.deviceId
}

/** 设备列表状态：[entries] 按首次出现的顺序排列，每个设备 ID 至多 1 项。 */
data class DeviceListState(
    val selfId: DeviceId,
    val interfaces: Set<LocalInterface>,
    val trusted: Set<DeviceId> = emptySet(),
    val entries: List<DeviceEntry> = emptyList(),
)

sealed interface DeviceListEvent {
    data class Announced(val a: Announcement, val src: Ipv4, val via: LocalInterface?, val nowMono: Long) : DeviceListEvent

    /** 每 1 s 一次。 */
    data class Tick(val nowMono: Long) : DeviceListEvent

    data class TrustChanged(val trusted: Set<DeviceId>) : DeviceListEvent
}

/** 设备列表归约（需求 5.2–5.5、5.8，设计 C3.4）。 */
object DeviceListReducer {
    /** 最近接受时间早于 now − 10 s 的项在 Tick 时移除（5.4）。 */
    const val EXPIRY_MS = 10_000L

    fun reduce(s: DeviceListState, e: DeviceListEvent): DeviceListState = when (e) {
        is DeviceListEvent.Announced -> {
            if (e.a.deviceId == s.selfId || !AddressRules.acceptsInbound(e.src, e.via, s.interfaces)) {
                s
            } else {
                val entry = DeviceEntry(e.a, e.src, e.nowMono, e.a.deviceId in s.trusted)
                val i = s.entries.indexOfFirst { it.deviceId == e.a.deviceId }
                s.copy(entries = if (i < 0) s.entries + entry else s.entries.toMutableList().also { it[i] = entry })
            }
        }
        is DeviceListEvent.Tick -> {
            val kept = s.entries.filter { it.lastSeenMono >= e.nowMono - EXPIRY_MS }
            if (kept.size == s.entries.size) s else s.copy(entries = kept)
        }
        is DeviceListEvent.TrustChanged -> s.copy(
            trusted = e.trusted,
            entries = s.entries.map { it.copy(trusted = it.deviceId in e.trusted) },
        )
    }

    /** 发现管线：acceptsInbound → AnnouncementCodec.decode → reduce；无效数据报不改变状态（含过期计时，23.2）。 */
    fun onDatagram(s: DeviceListState, datagram: ByteArray, src: Ipv4, via: LocalInterface?, nowMono: Long): DeviceListState {
        if (!AddressRules.acceptsInbound(src, via, s.interfaces)) return s
        val a = AnnouncementCodec.decode(datagram) ?: return s
        return reduce(s, DeviceListEvent.Announced(a, src, via, nowMono))
    }
}
