package com.lantransfer.app.storage

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.InterruptInfo
import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.domain.TaskRecord
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TempRef
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TransferTask
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.FileRecord
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.HistoryStore
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.RecordId
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TaskStore
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import java.time.LocalDateTime

class RoomTrustStore(private val db: LanTransferDatabase) : TrustStore {
    private val changesFlow = MutableSharedFlow<Unit>(extraBufferCapacity = 16)
    override val changes: Flow<Unit> = changesFlow.asSharedFlow()

    override fun all(): List<TrustedDevice> = runCatching {
        db.dao().allTrusted().map { it.toDomain() }
    }.getOrDefault(emptyList())

    override fun get(id: DeviceId): TrustedDevice? = runCatching {
        db.dao().getTrusted(id.value.toByteArray())?.toDomain()
    }.getOrNull()

    override fun upsert(d: TrustedDevice): StoreResult = runWrite {
        db.dao().upsertTrusted(d.toEntity())
        changesFlow.tryEmit(Unit)
    }

    override fun delete(id: DeviceId): StoreResult = runWrite {
        db.dao().deleteTrusted(id.value.toByteArray())
        changesFlow.tryEmit(Unit)
    }

    override fun setAutoAccept(id: DeviceId, on: Boolean): StoreResult = runWrite {
        db.dao().updateAutoAccept(id.value.toByteArray(), if (on) 1 else 0)
        changesFlow.tryEmit(Unit)
    }

    override fun updateName(id: DeviceId, name: String): StoreResult = runWrite {
        db.dao().updateTrustedName(id.value.toByteArray(), name)
        changesFlow.tryEmit(Unit)
    }

    override fun recordAddress(id: DeviceId, ip: Ipv4, port: Int): StoreResult = runWrite {
        db.dao().updateTrustedAddress(id.value.toByteArray(), ip.bits.toLong() and 0xFFFF_FFFFL, port)
        changesFlow.tryEmit(Unit)
    }
}

class RoomHistoryStore(private val db: LanTransferDatabase) : HistoryStore {
    override fun findText(peer: DeviceId, id: MessageId): TextRecord? = runCatching {
        db.dao().findText(peer.value.toByteArray(), id.value.toByteArray())?.toDomain()
    }.getOrNull()

    override fun appendText(r: TextRecord): StoreResult = try {
        var dup = false
        db.runInTransaction {
            val d = db.dao()
            if (d.findText(r.peer.value.toByteArray(), r.messageId.value.toByteArray()) != null) {
                dup = true
                return@runInTransaction
            }
            val seq = db.allocateSeq()
            val rowId = d.insertText(
                TextMessageEntity(
                    seq = seq,
                    peerId = r.peer.value.toByteArray(),
                    messageId = r.messageId.value.toByteArray(),
                    direction = r.direction.code,
                    peerName = r.peerName,
                    textUtf8 = r.textUtf8.toByteArray(),
                    sentAt = r.sentAtUtcMs,
                    recordedAt = r.recordedAt.utcMs,
                    recordedTz = r.recordedAt.offsetMin,
                    status = r.status.code,
                ),
            )
            if (rowId == -1L) dup = true
        }
        if (dup) StoreResult.Duplicate else StoreResult.Ok
    } catch (e: Throwable) {
        StoreResult.Failed(e.message ?: "写入文字记录失败")
    }

    override fun updateTextStatus(id: MessageId, peer: DeviceId, s: TextStatus): StoreResult = runWrite {
        db.dao().updateTextStatus(peer.value.toByteArray(), id.value.toByteArray(), s.code)
    }

    override fun upsertFile(r: FileRecord): StoreResult = runWrite {
        db.runInTransaction {
            val d = db.dao()
            val tidBytes = r.file.transferId.value.toByteArray()
            if (d.getTask(tidBytes) == null) {
                d.upsertTask(
                    TransferTaskEntity(
                        transferId = tidBytes,
                        peerId = r.peer.value.toByteArray(),
                        peerName = r.peerName,
                        direction = r.direction.code,
                        state = r.taskState.code,
                        interruptReason = r.interrupt?.reason?.code,
                        interruptDetail = r.interrupt?.detail,
                        receiveDir = null,
                        recordedAt = r.recordedAt.utcMs,
                        recordedTz = r.recordedAt.offsetMin,
                        updatedAt = r.recordedAt.utcMs,
                    ),
                )
            }
            val existing = d.getFile(tidBytes, r.file.index)
            val seq = existing?.seq ?: db.allocateSeq()
            d.upsertFile(r.file.toEntity(seq))
        }
    }

    override fun list(limit: Int): ListResult<HistoryRecord> = try {
        val d = db.dao()
        val texts = d.listTexts(limit).map { HistoryRecord.Text(it.toDomain()) }
        val tasksByKey = d.allTasks().associateBy { Bytes16.of(it.transferId) }
        val files = d.allFiles().mapNotNull { f ->
            val task = tasksByKey[Bytes16.of(f.transferId)] ?: return@mapNotNull null
            val interrupt = task.interruptReason?.let { code ->
                InterruptReason.fromCode(code)?.let { InterruptInfo(it, task.interruptDetail) }
            }
            HistoryRecord.File(
                FileRecord(
                    file = f.toDomain(),
                    peer = DeviceId(Bytes16.of(task.peerId)),
                    peerName = task.peerName,
                    direction = Direction.fromCode(task.direction) ?: Direction.RECEIVE,
                    recordedAt = RecordTime(task.recordedAt, task.recordedTz),
                    taskState = TaskState.fromCode(task.state) ?: TaskState.INTERRUPTED,
                    interrupt = interrupt,
                ),
            )
        }
        val merged = (texts + files)
            .sortedWith(compareBy<HistoryRecord>({ it.recordedAt.utcMs }, { it.seq }))
            .take(limit)
        ListResult.Ok(merged)
    } catch (e: Throwable) {
        ListResult.Failed(e.message ?: "读取传输记录失败")
    }

    override fun delete(id: RecordId): StoreResult = runWrite {
        db.dao().deleteText(id.seq)
    }

    override fun deleteFileRecord(id: TransferId, index: Int): StoreResult = runWrite {
        db.runInTransaction {
            val d = db.dao()
            val tidBytes = id.value.toByteArray()
            d.deleteFile(tidBytes, index)
            if (d.countFiles(tidBytes) == 0) {
                d.deleteTask(tidBytes)
            }
        }
    }

    override fun clearAll(): StoreResult = runWrite {
        db.runInTransaction {
            val d = db.dao()
            d.clearTexts()
            d.clearFiles()
            d.clearTasks()
        }
    }
}

class RoomTaskStore(
    private val db: LanTransferDatabase,
    private val clock: Clock,
) : TaskStore {
    override fun save(t: TaskRecord): StoreResult = runWrite {
        db.runInTransaction {
            val d = db.dao()
            val tidBytes = t.transferId.value.toByteArray()
            d.upsertTask(
                TransferTaskEntity(
                    transferId = tidBytes,
                    peerId = t.peer.value.toByteArray(),
                    peerName = t.peerName,
                    direction = t.direction.code,
                    state = t.state.code,
                    interruptReason = t.interrupt?.reason?.code,
                    interruptDetail = t.interrupt?.detail,
                    receiveDir = t.receiveDir?.serialize(),
                    recordedAt = t.recordedAt.utcMs,
                    recordedTz = t.recordedAt.offsetMin,
                    updatedAt = t.updatedAtUtcMs,
                ),
            )
            for (f in t.files) {
                val existing = d.getFile(tidBytes, f.index)
                val seq = existing?.seq ?: db.allocateSeq()
                d.upsertFile(f.toEntity(seq))
            }
        }
    }

    override fun get(id: TransferId): TaskRecord? = runCatching {
        val d = db.dao()
        val tidBytes = id.value.toByteArray()
        val task = d.getTask(tidBytes) ?: return@runCatching null
        val files = d.filesForTask(tidBytes).map { it.toDomain() }
        task.toDomain(files)
    }.getOrNull()

    override fun unfinished(): List<TaskRecord> = runCatching {
        val d = db.dao()
        d.unfinishedTasks().map { t ->
            val files = d.filesForTask(t.transferId).map { it.toDomain() }
            t.toDomain(files)
        }
    }.getOrDefault(emptyList())

    override fun addPendingCancel(peer: DeviceId, id: TransferId): StoreResult = runWrite {
        db.dao().insertPendingCancel(
            PendingCancelEntity(
                peerId = peer.value.toByteArray(),
                transferId = id.value.toByteArray(),
                createdAt = clock.wallMs(),
            ),
        )
    }

    override fun takePendingCancels(peer: DeviceId): List<TransferId> = runCatching {
        var result: List<TransferId> = emptyList()
        db.runInTransaction {
            val d = db.dao()
            val peerBytes = peer.value.toByteArray()
            val rows = d.getPendingCancels(peerBytes)
            result = rows.map { TransferId(Bytes16.of(it.transferId)) }
            if (rows.isNotEmpty()) {
                d.deletePendingCancels(peerBytes)
            }
        }
        result
    }.getOrDefault(emptyList())
}

private inline fun runWrite(block: () -> Unit): StoreResult = try {
    block()
    StoreResult.Ok
} catch (e: Throwable) {
    StoreResult.Failed(e.message ?: "数据库写入失败")
}

private fun TrustedDeviceEntity.toDomain(): TrustedDevice = TrustedDevice(
    deviceId = DeviceId(Bytes16.of(deviceId)),
    displayName = displayName,
    deviceType = DeviceType.fromCode(deviceType) ?: DeviceType.ANDROID,
    publicKey = Bytes.of(publicKey),
    autoAccept = autoAccept != 0,
    pairedAtUtcMs = pairedAt,
    lastAddress = lastIp?.let { Ipv4(it.toInt()) },
    lastPort = lastPort,
)

private fun TrustedDevice.toEntity(): TrustedDeviceEntity = TrustedDeviceEntity(
    deviceId = deviceId.value.toByteArray(),
    displayName = displayName,
    deviceType = deviceType.code,
    publicKey = publicKey.toByteArray(),
    autoAccept = if (autoAccept) 1 else 0,
    pairedAt = pairedAtUtcMs,
    lastIp = lastAddress?.let { it.bits.toLong() and 0xFFFF_FFFFL },
    lastPort = lastPort,
)

private fun TextMessageEntity.toDomain(): TextRecord = TextRecord(
    seq = seq,
    peer = DeviceId(Bytes16.of(peerId)),
    peerName = peerName,
    messageId = MessageId(Bytes16.of(messageId)),
    direction = Direction.fromCode(direction) ?: Direction.RECEIVE,
    textUtf8 = Bytes.of(textUtf8),
    sentAtUtcMs = sentAt,
    recordedAt = RecordTime(recordedAt, recordedTz),
    status = TextStatus.fromCode(status) ?: TextStatus.RECEIVED,
)

private fun TransferTaskEntity.toDomain(files: List<TransferFile>): TransferTask = TransferTask(
    transferId = TransferId(Bytes16.of(transferId)),
    peer = DeviceId(Bytes16.of(peerId)),
    peerName = peerName,
    direction = Direction.fromCode(direction) ?: Direction.RECEIVE,
    state = TaskState.fromCode(state) ?: TaskState.INTERRUPTED,
    interrupt = interruptReason?.let { code -> InterruptReason.fromCode(code)?.let { InterruptInfo(it, interruptDetail) } },
    receiveDir = receiveDir?.let { DirRef.parse(it) },
    recordedAt = RecordTime(recordedAt, recordedTz),
    updatedAtUtcMs = updatedAt,
    files = files,
)

private fun TransferFileEntity.toDomain(): TransferFile = TransferFile(
    transferId = TransferId(Bytes16.of(transferId)),
    index = fileIndex,
    seq = seq,
    originalName = originalName,
    size = size,
    mime = mime,
    mtime = mtime,
    state = FileState.fromCode(state) ?: FileState.WAITING,
    persisted = persisted,
    sha256 = sha256?.let { Bytes.of(it) },
    sourceRef = sourceRef?.let { SourceRef(it) },
    tempRef = tempRef?.let { TempRef(it) },
    startedAt = startedAt?.let { runCatching { LocalDateTime.parse(it) }.getOrNull() },
    lastWriteAtUtcMs = lastWriteAt,
    finalName = finalName,
    fullPath = fullPath,
    storedRef = storedRef?.let { StoredFileRef(it) },
)

private fun TransferFile.toEntity(seq: Long): TransferFileEntity = TransferFileEntity(
    transferId = transferId.value.toByteArray(),
    fileIndex = index,
    seq = seq,
    originalName = originalName,
    size = size,
    mime = mime,
    mtime = mtime,
    state = state.code,
    persisted = persisted,
    sha256 = sha256?.toByteArray(),
    sourceRef = sourceRef?.value,
    tempRef = tempRef?.value,
    startedAt = startedAt?.toString(),
    lastWriteAt = lastWriteAtUtcMs,
    finalName = finalName,
    fullPath = fullPath,
    storedRef = storedRef?.value,
)
