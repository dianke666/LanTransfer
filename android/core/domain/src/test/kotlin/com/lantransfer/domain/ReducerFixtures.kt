package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.TransferId
import java.time.LocalDateTime
import kotlin.random.Random

// 归约器测试共用的构造函数（Property 35–38 与状态迁移表测试）。

internal fun deviceIdOf(b: Int): DeviceId = DeviceId(Bytes16.of(ByteArray(16) { b.toByte() }))

internal fun bytes32(b: Int): Bytes = Bytes.of(ByteArray(32) { (b + it).toByte() })

internal fun peerInfo(b: Int, type: DeviceType = DeviceType.ANDROID): PeerInfo =
    PeerInfo(deviceIdOf(b), "设备$b", type, bytes32(b * 7), Ipv4.of(192, 168, 1, b and 0xFF), 42580)

internal val fixtureTransferId = TransferId(Bytes16.of(ByteArray(16) { 0x5A }))

internal val fixtureLocalNow: LocalDateTime = LocalDateTime.of(2026, 10, 4, 9, 30, 5)

internal fun transferTask(direction: Direction, state: TaskState, sizes: List<Long>): TransferTask = TransferTask(
    fixtureTransferId,
    deviceIdOf(9),
    "对方",
    direction,
    state,
    if (state == TaskState.INTERRUPTED) InterruptInfo(InterruptReason.PEER_CLOSED) else null,
    if (direction == Direction.RECEIVE && state != TaskState.AWAITING_USER) DirRef.Default else null,
    RecordTime(1_700_000_000_000, 480),
    1_700_000_000_000,
    sizes.mapIndexed { i, size ->
        TransferFile(fixtureTransferId, i, i.toLong(), "f$i.bin", size, "application/octet-stream", 1_600_000_000_000, FileState.WAITING)
    },
)

internal fun Random.bytes(n: Int): Bytes = Bytes.of(nextBytes(n))
