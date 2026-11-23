package com.lantransfer.engine.testing

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.PendingCancel
import com.lantransfer.domain.Rng
import com.lantransfer.domain.Settings
import com.lantransfer.domain.TaskRecord
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TransferTask
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.FileRecord
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.HistoryStore
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.ports.IdentityStore
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.RecordId
import com.lantransfer.engine.ports.SettingsStore
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TaskStore
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import com.lantransfer.protocol.X25519KeyPair
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

// 内存版存储（设计 C5.3、M2）：语义与 SQLite 实现一致，也是 Property 48、49 的参考模型。
// 每个写方法都可以通过 failWrites(操作名) 注入失败（20.8）；失败时不做任何改动。

private val INJECTED = StoreResult.Failed("injected failure")

/** 身份存储：[initial] 为已保存的身份；[corrupted] 为 true 时模拟 identity.bin 损坏（4.6）。显示名称独立保存，身份重置后保留。 */
class InMemoryIdentityStore(
    private val rng: Rng,
    defaultName: String,
    initial: DeviceIdentity? = null,
    private var corrupted: Boolean = false,
) : IdentityStore {
    private var current: DeviceIdentity? = initial
    private var name: String = initial?.displayName ?: defaultName
    var failWrites = false

    /** 为 true 时新生成的身份无法保存：loadOrCreate 返回 SaveFailed，状态不变（Q14）。 */
    var failSaves = false

    /** loadOrCreate 的调用次数。 */
    var loads = 0
        private set

    override fun loadOrCreate(): IdentityLoad {
        loads++
        val existing = current
        if (existing != null && !corrupted) return IdentityLoad.Loaded(existing.withName())
        if (failSaves) return IdentityLoad.SaveFailed("injected failure", corrupted)
        val created = DeviceIdentity(DeviceId(Bytes16.of(rng.nextBytes(16))), X25519KeyPair.fromPrivateKey(rng.nextBytes(32)), name)
        current = created
        val wasCorrupted = corrupted
        corrupted = false
        return if (wasCorrupted) IdentityLoad.ResetCorrupted(created) else IdentityLoad.Created(created)
    }

    private fun DeviceIdentity.withName() = DeviceIdentity(deviceId, keyPair, name)

    override fun displayName(): String = name

    override fun setDisplayName(name: String): StoreResult {
        if (failWrites) return INJECTED
        this.name = name
        return StoreResult.Ok
    }
}

class InMemoryTrustStore : TrustStore {
    private val devices = LinkedHashMap<DeviceId, TrustedDevice>()
    private val changeFlow = MutableSharedFlow<Unit>(extraBufferCapacity = 64)
    var failWrites: (String) -> Boolean = { false }

    override val changes: Flow<Unit> get() = changeFlow

    private inline fun write(op: String, block: () -> Unit): StoreResult {
        if (failWrites(op)) return INJECTED
        synchronized(this) { block() }
        changeFlow.tryEmit(Unit)
        return StoreResult.Ok
    }

    override fun all(): List<TrustedDevice> = synchronized(this) { devices.values.toList() }

    override fun get(id: DeviceId): TrustedDevice? = synchronized(this) { devices[id] }

    override fun upsert(d: TrustedDevice): StoreResult = write("upsert") { devices[d.deviceId] = d }

    override fun delete(id: DeviceId): StoreResult = write("delete") { devices.remove(id) }

    override fun setAutoAccept(id: DeviceId, on: Boolean): StoreResult =
        write("setAutoAccept") { devices[id]?.let { devices[id] = it.copy(autoAccept = on) } }

    override fun updateName(id: DeviceId, name: String): StoreResult =
        write("updateName") { devices[id]?.let { devices[id] = it.copy(displayName = name) } }

    override fun recordAddress(id: DeviceId, ip: Ipv4, port: Int): StoreResult =
        write("recordAddress") { devices[id]?.let { devices[id] = it.copy(lastAddress = ip, lastPort = port) } }
}

/**
 * HistoryStore 与 TaskStore 共用的内存“数据库”（M2）：text_message、transfer_task + transfer_file、pending_cancel，
 * 以及 next_history_seq。文字记录和文件记录共用 seq 计数器。
 */
class InMemoryDatabase(private val wallMs: () -> Long = { 0L }) {
    internal var nextSeq = 1L
    internal val texts = LinkedHashMap<Pair<DeviceId, MessageId>, TextRecord>()

    /** 任务行；files 按 index 排序。 */
    internal val tasks = LinkedHashMap<TransferId, TransferTask>()
    internal val pendingCancels = LinkedHashMap<Pair<DeviceId, TransferId>, PendingCancel>()

    /** 返回 true 时该次写入失败（参数为操作名，如 "appendText"、"save"）。 */
    var failWrites: (String) -> Boolean = { false }
    var failReads = false

    internal fun now() = wallMs()

    internal inline fun <T> tx(block: InMemoryDatabase.() -> T): T = synchronized(this) { block() }

    /** 快照：当前的全部 pending_cancel 行。 */
    fun pendingCancels(): List<PendingCancel> = tx { pendingCancels.values.toList() }

    /** 把 [files] 合并进任务行：已存在的行保留 seq，新行按 index 顺序分配 seq。 */
    internal fun mergeFiles(existing: List<TransferFile>, files: List<TransferFile>): List<TransferFile> {
        val byIndex = existing.associateBy { it.index }.toMutableMap()
        for (f in files.sortedBy { it.index }) {
            val old = byIndex[f.index]
            byIndex[f.index] = f.copy(seq = old?.seq ?: nextSeq++)
        }
        return byIndex.values.sortedBy { it.index }
    }
}

class InMemoryHistoryStore(private val db: InMemoryDatabase) : HistoryStore {
    private inline fun write(op: String, block: InMemoryDatabase.() -> StoreResult): StoreResult =
        if (db.failWrites(op)) INJECTED else db.tx(block)

    override fun findText(peer: DeviceId, id: MessageId): TextRecord? = db.tx { texts[peer to id] }

    override fun appendText(r: TextRecord): StoreResult = write("appendText") {
        val key = r.peer to r.messageId
        if (key in texts) {
            StoreResult.Duplicate
        } else {
            texts[key] = r.copy(seq = nextSeq++)
            StoreResult.Ok
        }
    }

    override fun updateTextStatus(id: MessageId, peer: DeviceId, s: TextStatus): StoreResult = write("updateTextStatus") {
        texts[peer to id]?.let { texts[peer to id] = it.copy(status = s) }
        StoreResult.Ok
    }

    override fun upsertFile(r: FileRecord): StoreResult = write("upsertFile") {
        val id = r.file.transferId
        val task = tasks[id] ?: TransferTask(id, r.peer, r.peerName, r.direction, r.taskState, r.interrupt, null, r.recordedAt, now(), emptyList())
        tasks[id] = task.copy(files = mergeFiles(task.files, listOf(r.file)))
        StoreResult.Ok
    }

    override fun list(limit: Int): ListResult<HistoryRecord> {
        if (db.failReads) return ListResult.Failed("injected failure")
        return db.tx {
            val all = ArrayList<HistoryRecord>()
            texts.values.mapTo(all) { HistoryRecord.Text(it) }
            for (t in tasks.values) {
                t.files.mapTo(all) { f -> HistoryRecord.File(FileRecord(f, t.peer, t.peerName, t.direction, t.recordedAt, t.state, t.interrupt)) }
            }
            ListResult.Ok(all.sortedWith(compareBy<HistoryRecord>({ it.recordedAt.utcMs }, { it.seq })).take(limit))
        }
    }

    override fun delete(id: RecordId): StoreResult = write("delete") {
        texts.entries.removeAll { it.value.seq == id.seq }
        StoreResult.Ok
    }

    override fun deleteFileRecord(id: TransferId, index: Int): StoreResult = write("deleteFileRecord") {
        tasks[id]?.let { t ->
            val rest = t.files.filter { it.index != index }
            if (rest.isEmpty()) tasks.remove(id) else tasks[id] = t.copy(files = rest)
        }
        StoreResult.Ok
    }

    override fun clearAll(): StoreResult = write("clearAll") {
        texts.clear()
        tasks.clear()
        StoreResult.Ok
    }
}

class InMemoryTaskStore(private val db: InMemoryDatabase) : TaskStore {
    override fun save(t: TaskRecord): StoreResult {
        if (db.failWrites("save")) return INJECTED
        return db.tx {
            val existing = tasks[t.transferId]?.files ?: emptyList()
            tasks[t.transferId] = t.copy(files = mergeFiles(existing, t.files))
            StoreResult.Ok
        }
    }

    override fun get(id: TransferId): TaskRecord? = db.tx { tasks[id] }

    override fun unfinished(): List<TaskRecord> = db.tx { tasks.values.filter { !it.state.isTerminal } }

    override fun addPendingCancel(peer: DeviceId, id: TransferId): StoreResult {
        if (db.failWrites("addPendingCancel")) return INJECTED
        return db.tx {
            pendingCancels.getOrPut(peer to id) { PendingCancel(peer, id, now()) }
            StoreResult.Ok
        }
    }

    override fun takePendingCancels(peer: DeviceId): List<TransferId> = db.tx {
        val mine = pendingCancels.values.filter { it.peer == peer }
        mine.forEach { pendingCancels.remove(it.peer to it.transferId) }
        mine.map { it.transferId }
    }
}

class InMemorySettingsStore(initial: Settings = Settings()) : SettingsStore {
    private val flow = MutableStateFlow(initial)
    var failWrites = false

    override val changes: StateFlow<Settings> get() = flow

    override fun current(): Settings = flow.value

    override fun update(transform: (Settings) -> Settings): StoreResult {
        if (failWrites) return INJECTED
        flow.value = transform(flow.value)
        return StoreResult.Ok
    }
}
